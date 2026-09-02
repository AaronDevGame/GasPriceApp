using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

public class AuthService
{
    private const int SecretByteLength = 32;
    private const int EncodedSecretLength = 43;
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromHours(1);

    public string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretByteLength);
        return Base64UrlEncode(bytes);
    }

    public string HashSecret(string secret)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));
        return Base64UrlEncode(hash);
    }

    public bool VerifySecret(string secret, string storedHash)
    {
        if (secret.Length != EncodedSecretLength || storedHash.Length != EncodedSecretLength)
            return false;

        var candidateHash = HashSecret(secret);
        return FixedTimeEquals(candidateHash, storedHash);
    }

    public bool VerifyLegacyToken(string token, string storedToken)
    {
        var candidateHash = HashSecret(token);
        var storedTokenHash = HashSecret(storedToken);
        return FixedTimeEquals(candidateHash, storedTokenHash);
    }

    public bool IsValidAccessToken(Guest guest, string token, DateTime now)
    {
        if (guest.AccessTokenHash is not null && guest.AccessTokenExpiresAt > now)
            return VerifySecret(token, guest.AccessTokenHash);

        // Existing deployments stored a deterministic token in plaintext. Keep it
        // valid only until the account performs its one-time credential upgrade.
        return guest.GuestCredentialHash is null &&
               guest.LegacyToken is not null &&
               VerifyLegacyToken(token, guest.LegacyToken);
    }

    public DateTime GetAccessTokenExpiration(DateTime issuedAt) =>
        issuedAt.Add(AccessTokenLifetime);

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

public record AuthResult
{
    public string DeviceId { get; init; } = "";
    public long PlayerId { get; init; }
    public string PlayerName { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public DateTime AccessTokenExpiresAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GuestCredential { get; init; }
    public TokenType TokenType { get; init; } = TokenType.Guest;
    public DateTime CreatedAt { get; init; }
    public bool IsNewAccount { get; init; }
    public bool IsLoggedIn { get; init; }
}

public record LoginRequest
{
    public string? PlayerName { get; init; }
}

public record AuthStatusResult(
    bool HasGuestLogin,
    bool IsLoggedIn,
    string? PlayerName = null,
    long? PlayerId = null,
    string? AccountType = null,
    DateTime? CreatedAt = null);

public record LogoutResult(bool IsLoggedIn);

public static class AuthEndpoints
{
    private const long MinPlayerId = 100_000_000_000_000;
    private const long MaxPlayerIdExclusive = 1_000_000_000_000_000;
    private const int MaxPlayerIdAttempts = 10;
    public const string DeviceIdHeader = "X-Device-Id";
    public const string GuestCredentialHeader = "X-Guest-Credential";
    public const string DeviceIdCookie = "device_id";

    public static void MapAuthEndpoints(this WebApplication app, AuthService auth, string instanceId)
    {
        app.MapGet(ApiRoutes.AuthStatus, GetAuthStatusAsync);
        app.MapPost(ApiRoutes.AuthGuestLogin, LoginAsync);
        app.MapPost(ApiRoutes.AuthLogout, LogoutAsync);

        async Task<IResult> GetAuthStatusAsync(HttpRequest request, AppDbContext db)
        {
            if (!request.Headers.TryGetValue("Authorization", out var authHeader))
                return ApiResults.Unauthorized(AuthErrors.MissingAuthorizationHeader, instanceId);

            if (!request.Headers.TryGetValue(DeviceIdHeader, out var deviceIdHeader) ||
                string.IsNullOrWhiteSpace(deviceIdHeader.ToString()))
                return ApiResults.Unauthorized(AuthErrors.MissingDeviceIdHeader, instanceId);

            var parts = authHeader.ToString().Split(' ', 2);
            if (parts.Length != 2 ||
                !parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(parts[1]))
                return ApiResults.Unauthorized(AuthErrors.InvalidPlayerToken, instanceId);

            var token = parts[1].Trim();
            var deviceId = deviceIdHeader.ToString().Trim();
            var guest = await db.Guests
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.DeviceId == deviceId);

            if (guest is null)
                return ApiResults.Unauthorized(AuthErrors.InvalidPlayerCredentials, instanceId);

            if (!guest.IsLoggedIn)
                return ApiResults.Unauthorized(AuthErrors.PlayerNotLoggedIn, instanceId);

            if (guest.AccessTokenHash is not null && guest.AccessTokenExpiresAt <= DateTime.UtcNow)
                return ApiResults.Unauthorized(AuthErrors.AccessTokenExpired, instanceId);

            if (!auth.IsValidAccessToken(guest, token, DateTime.UtcNow))
                return ApiResults.Unauthorized(AuthErrors.InvalidPlayerCredentials, instanceId);

            return ApiResults.Ok(
                new AuthStatusResult(true, guest.IsLoggedIn, guest.PlayerName, guest.PlayerId, "guest", guest.CreatedAt),
                "auth_status",
                instanceId);
        }

        async Task<IResult> LoginAsync(HttpRequest request, HttpResponse response, AppDbContext db)
        {
            var deviceId = ResolveDeviceId(request, response);

            // Upsert the guest record and record this login.
            var now = DateTime.UtcNow;
            var userAgent = request.Headers.UserAgent.ToString();

            var guest = await db.Guests.FindAsync(deviceId);
            var isNewAccount = guest is null;
            var alreadyLoggedIn = guest?.IsLoggedIn == true;
            string? issuedGuestCredential = null;
            if (guest is null)
            {
                LoginRequest? loginRequest;
                try
                {
                    loginRequest = await ReadLoginRequestAsync(request);
                }
                catch (BadHttpRequestException ex)
                {
                    return ApiResults.BadRequest(ex.Message, instanceId);
                }

                var resolvedName = await PlayerNameService.ResolveInitialNameAsync(
                    db,
                    deviceId,
                    loginRequest?.PlayerName);
                if (!resolvedName.IsValid)
                    return ApiResults.BadRequest(resolvedName.Error, instanceId);

                guest = new Guest
                {
                    DeviceId = deviceId,
                    PlayerId = await GenerateUniquePlayerIdAsync(db),
                    PlayerName = resolvedName.Name,
                    CreatedAt = now
                };

                issuedGuestCredential = auth.GenerateSecret();
                guest.GuestCredentialHash = auth.HashSecret(issuedGuestCredential);
                db.Guests.Add(guest);
            }
            else
            {
                if (guest.GuestCredentialHash is null)
                {
                    if (!TryGetBearerToken(request, out var legacyToken) ||
                        guest.LegacyToken is null ||
                        !auth.VerifyLegacyToken(legacyToken, guest.LegacyToken))
                    {
                        return ApiResults.Unauthorized(
                            AuthErrors.LegacyTokenRequired,
                            instanceId,
                            "This existing account must provide its previous bearer token once to enable secure guest credentials.");
                    }

                    issuedGuestCredential = auth.GenerateSecret();
                    guest.GuestCredentialHash = auth.HashSecret(issuedGuestCredential);
                    guest.LegacyToken = null;
                }
                else
                {
                    if (!request.Headers.TryGetValue(GuestCredentialHeader, out var credentialHeader) ||
                        string.IsNullOrWhiteSpace(credentialHeader.ToString()))
                        return ApiResults.Unauthorized(AuthErrors.MissingGuestCredential, instanceId);

                    var guestCredential = credentialHeader.ToString().Trim();
                    if (!auth.VerifySecret(guestCredential, guest.GuestCredentialHash))
                        return ApiResults.Unauthorized(AuthErrors.InvalidGuestCredential, instanceId);
                }

                // PlayerName is creation-only on this endpoint. Existing guests
                // must use PATCH /player/profile to rename themselves.
            }

            var accessToken = auth.GenerateSecret();
            var accessTokenExpiresAt = auth.GetAccessTokenExpiration(now);
            guest.AccessTokenHash = auth.HashSecret(accessToken);
            guest.AccessTokenExpiresAt = accessTokenExpiresAt;
            guest.LegacyToken = null;
            guest.IpAddress = request.HttpContext.Connection.RemoteIpAddress?.ToString();
            guest.UserAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent;
            guest.DeviceType = DetectDeviceType(userAgent);
            guest.LastLoginAt = now;
            guest.LoginCount += 1;
            guest.IsLoggedIn = true;

            await PlayerDataStore.EnsureForGuestAsync(db, guest, now);
            await db.SaveChangesAsync();

            return ApiResults.Ok(
                new AuthResult
                {
                    DeviceId = deviceId,
                    PlayerId = guest.PlayerId,
                    PlayerName = guest.PlayerName,
                    AccessToken = accessToken,
                    AccessTokenExpiresAt = accessTokenExpiresAt,
                    GuestCredential = issuedGuestCredential,
                    CreatedAt = guest.CreatedAt,
                    IsNewAccount = isNewAccount,
                    IsLoggedIn = true
                },
                alreadyLoggedIn ? "already_logged_in" : "guest_login",
                instanceId);
        }

        // Ends the persisted session. The device_id is intentionally kept, so
        // re-login preserves the guest identity.
        async Task<IResult> LogoutAsync(HttpRequest request, AppDbContext db)
        {
            if (!request.Headers.TryGetValue("Authorization", out var authHeader))
                return ApiResults.Unauthorized(AuthErrors.MissingAuthorizationHeader, instanceId);

            if (!request.Headers.TryGetValue(DeviceIdHeader, out var deviceIdHeader) ||
                string.IsNullOrWhiteSpace(deviceIdHeader.ToString()))
                return ApiResults.Unauthorized(AuthErrors.MissingDeviceIdHeader, instanceId);

            var parts = authHeader.ToString().Split(' ', 2);
            if (parts.Length != 2 ||
                !parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(parts[1]))
                return ApiResults.Unauthorized(AuthErrors.InvalidPlayerToken, instanceId);

            var token = parts[1].Trim();
            var deviceId = deviceIdHeader.ToString().Trim();
            var guest = await db.Guests.FindAsync(deviceId);
            if (guest is null || !auth.IsValidAccessToken(guest, token, DateTime.UtcNow))
                return ApiResults.Unauthorized(AuthErrors.InvalidPlayerCredentials, instanceId);

            if (guest.IsLoggedIn)
            {
                guest.IsLoggedIn = false;
                guest.AccessTokenHash = null;
                guest.AccessTokenExpiresAt = null;
                guest.LegacyToken = null;
                guest.LastLogoutAt = DateTime.UtcNow;
                guest.LogoutCount += 1;
                await db.SaveChangesAsync();
            }

            return ApiResults.Ok(new LogoutResult(false), "logged_out", instanceId);
        }
    }

    private static bool TryGetBearerToken(HttpRequest request, out string token)
    {
        token = "";
        if (!request.Headers.TryGetValue("Authorization", out var authHeader))
            return false;

        var parts = authHeader.ToString().Split(' ', 2);
        if (parts.Length != 2 ||
            !parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(parts[1]))
            return false;

        token = parts[1].Trim();
        return true;
    }

    private static async Task<long> GenerateUniquePlayerIdAsync(AppDbContext db)
    {
        for (var attempt = 0; attempt < MaxPlayerIdAttempts; attempt++)
        {
            var playerId = GeneratePlayerId();
            if (!await db.Guests.AnyAsync(g => g.PlayerId == playerId))
                return playerId;
        }

        throw new InvalidOperationException("Could not generate a unique player ID.");
    }

    private static long GeneratePlayerId()
    {
        var range = (ulong)(MaxPlayerIdExclusive - MinPlayerId);
        var limit = ulong.MaxValue - (ulong.MaxValue % range);
        var bytes = new byte[sizeof(ulong)];

        while (true)
        {
            RandomNumberGenerator.Fill(bytes);
            var value = BitConverter.ToUInt64(bytes, 0);

            if (value < limit)
                return MinPlayerId + (long)(value % range);
        }
    }

    // Resolves a stable identifier for the calling device, no frontend required.
    // Order of preference:
    //   1. X-Device-Id header  (a real client can supply its own stable id)
    //   2. device_id cookie    (set by us on a previous request)
    //   3. a freshly minted GUID, persisted as a cookie so the same device is
    //      recognized next time (browsers and Postman resend cookies automatically)
    // Stable per-installation identity: X-Device-Id header, then the device_id
    // cookie, then a freshly minted GUID persisted as a cookie. This identifier
    // persists across logout but is never used as an authentication secret.
    private static string ResolveDeviceId(HttpRequest request, HttpResponse response)
    {
        if (request.Headers.TryGetValue(DeviceIdHeader, out var headerValue) &&
            !string.IsNullOrWhiteSpace(headerValue.ToString()))
            return headerValue.ToString();

        if (request.Cookies.TryGetValue(DeviceIdCookie, out var cookieValue) &&
            !string.IsNullOrWhiteSpace(cookieValue))
            return cookieValue;

        var newDeviceId = Guid.NewGuid().ToString();
        response.Cookies.Append(DeviceIdCookie, newDeviceId, DeviceCookieOptions(request));
        return newDeviceId;
    }

    private static async Task<LoginRequest?> ReadLoginRequestAsync(HttpRequest request)
    {
        if (request.ContentLength == 0 ||
            (request.ContentLength is null && string.IsNullOrWhiteSpace(request.ContentType)))
            return null;

        if (!request.HasJsonContentType())
            throw new BadHttpRequestException("Login request body must be JSON.");

        try
        {
            return await request.ReadFromJsonAsync<LoginRequest>();
        }
        catch (JsonException)
        {
            throw new BadHttpRequestException("Login request body must be valid JSON.");
        }
    }

    private static CookieOptions DeviceCookieOptions(HttpRequest request) => new()
    {
        HttpOnly = true,
        Secure = request.IsHttps,   // HTTPS in production (Render); still works on http locally
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromDays(365),
        IsEssential = true
    };

    // Best-effort device/platform category from the User-Agent. Browsers parse
    // reliably; native apps may send a generic UA or none, so this can be "Unknown".
    private static string DetectDeviceType(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "Unknown";

        var ua = userAgent.ToLowerInvariant();
        if (ua.Contains("iphone")) return "iPhone";
        if (ua.Contains("ipad")) return "iPad";
        if (ua.Contains("android")) return "Android";
        if (ua.Contains("windows")) return "Windows";
        if (ua.Contains("mac os") || ua.Contains("macintosh")) return "Mac";
        if (ua.Contains("linux")) return "Linux";
        if (ua.Contains("unity")) return "Unity";
        if (ua.Contains("postman")) return "Postman";
        if (ua.Contains("curl")) return "curl";
        if (ua.Contains("mozilla")) return "Browser";
        return "Unknown";
    }

}
