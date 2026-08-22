using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

public class AuthService
{
    private readonly byte[] _secret;

    public AuthService(IConfiguration config)
    {
        // HMAC signing key. Set AUTH_SECRET in the environment for anything real;
        // the fallback is only meant for local development.
        var secret = config["AUTH_SECRET"] ?? "dev-insecure-secret-change-me";
        _secret = Encoding.UTF8.GetBytes(secret);
    }

    // Deterministic, stateless token: the same deviceId always produces the same
    // token, so logging out and back in returns the same value. Nothing is stored
    // server-side, so it also survives process restarts.
    public string GenerateToken(string deviceId)
    {
        using var hmac = new HMACSHA256(_secret);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(deviceId));
        return Base64UrlEncode(hash);
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
    public string Token { get; init; } = "";
    public string TokenType { get; init; } = "guest";
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
    private const int MaxPlayerNameLength = 24;
    private const int MinDefaultPlayerNameNumber = 1000;
    private const int MaxDefaultPlayerNameNumberExclusive = 10000;
    private const int MaxPlayerNameAttempts = 20;
    private const string DefaultPlayerNamePrefix = "Player ";

    public const string DeviceIdHeader = "X-Device-Id";
    public const string DeviceIdCookie = "device_id";
    public const string SessionCookie = "session_active";

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

            if (guest is null || guest.Token != token)
                return ApiResults.Unauthorized(AuthErrors.InvalidPlayerCredentials, instanceId);

            return ApiResults.Ok(
                new AuthStatusResult(true, guest.IsLoggedIn, guest.PlayerName, guest.PlayerId, "guest", guest.CreatedAt),
                "auth_status",
                instanceId);
        }

        async Task<IResult> LoginAsync(HttpRequest request, HttpResponse response, AppDbContext db)
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

            var deviceId = ResolveDeviceId(request, response);
            var token = auth.GenerateToken(deviceId);

            // Upsert the guest record and record this login.
            var now = DateTime.UtcNow;
            var userAgent = request.Headers.UserAgent.ToString();

            var guest = await db.Guests.FindAsync(deviceId);
            var isNewAccount = guest is null;
            var alreadyLoggedIn = guest?.IsLoggedIn == true;
            if (guest is null)
            {
                var resolvedName = await ResolvePlayerNameAsync(db, deviceId, loginRequest?.PlayerName, null);
                if (!resolvedName.IsValid)
                    return ApiResults.BadRequest(resolvedName.Error, instanceId);

                guest = new Guest
                {
                    DeviceId = deviceId,
                    PlayerId = await GenerateUniquePlayerIdAsync(db),
                    PlayerName = resolvedName.Name,
                    CreatedAt = now
                };
                db.Guests.Add(guest);
            }
            else
            {
                var resolvedName = await ResolvePlayerNameAsync(db, deviceId, loginRequest?.PlayerName, guest.PlayerName);
                if (!resolvedName.IsValid)
                    return ApiResults.BadRequest(resolvedName.Error, instanceId);

                guest.PlayerName = resolvedName.Name;
            }

            guest.Token = token;
            guest.IpAddress = request.HttpContext.Connection.RemoteIpAddress?.ToString();
            guest.UserAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent;
            guest.DeviceType = DetectDeviceType(userAgent);
            guest.LastLoginAt = now;
            guest.LoginCount += 1;
            guest.IsLoggedIn = true;

            await PlayerDataStore.EnsureForGuestAsync(db, guest, now);
            await db.SaveChangesAsync();
            response.Cookies.Append(SessionCookie, "1", SessionCookieOptions(request));

            return ApiResults.Ok(
                new AuthResult
                {
                    DeviceId = deviceId,
                    PlayerId = guest.PlayerId,
                    PlayerName = guest.PlayerName,
                    Token = token,
                    CreatedAt = guest.CreatedAt,
                    IsNewAccount = isNewAccount,
                    IsLoggedIn = true
                },
                alreadyLoggedIn ? "already_logged_in" : "guest_login",
                instanceId);
        }

        // Ends the persisted session and clears the cookie marker. The device_id
        // is intentionally kept, so re-login preserves the guest identity.
        async Task<IResult> LogoutAsync(HttpRequest request, HttpResponse response, AppDbContext db)
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
            if (guest is null || guest.Token != token)
                return ApiResults.Unauthorized(AuthErrors.InvalidPlayerCredentials, instanceId);

            response.Cookies.Delete(SessionCookie);

            if (guest.IsLoggedIn)
            {
                guest.IsLoggedIn = false;
                guest.LastLogoutAt = DateTime.UtcNow;
                guest.LogoutCount += 1;
                await db.SaveChangesAsync();
            }

            return ApiResults.Ok(new LogoutResult(false), "logged_out", instanceId);
        }
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
    // Stable per-device identity: X-Device-Id header, then the device_id cookie,
    // then a freshly minted GUID persisted as a cookie. This persists across
    // logout so the derived token stays the same.
    private static string ResolveDeviceId(HttpRequest request, HttpResponse response)
    {
        if (request.Headers.TryGetValue(DeviceIdHeader, out var headerValue) &&
            !string.IsNullOrWhiteSpace(headerValue.ToString()))
            return headerValue.ToString();

        if (request.Cookies.TryGetValue(DeviceIdCookie, out var cookieValue) &&
            !string.IsNullOrWhiteSpace(cookieValue))
            return cookieValue;

        var newDeviceId = Guid.NewGuid().ToString();
        response.Cookies.Append(DeviceIdCookie, newDeviceId, SessionCookieOptions(request));
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

    private static async Task<PlayerNameResult> ResolvePlayerNameAsync(
        AppDbContext db,
        string deviceId,
        string? requestedPlayerName,
        string? currentPlayerName)
    {
        if (!string.IsNullOrWhiteSpace(requestedPlayerName))
            return await ResolveRequestedPlayerNameAsync(db, deviceId, requestedPlayerName);

        if (!string.IsNullOrWhiteSpace(currentPlayerName))
            return PlayerNameResult.Valid(currentPlayerName.Trim());

        return PlayerNameResult.Valid(await GenerateUniqueDefaultPlayerNameAsync(db));
    }

    private static async Task<PlayerNameResult> ResolveRequestedPlayerNameAsync(
        AppDbContext db,
        string deviceId,
        string requestedPlayerName)
    {
        var playerName = requestedPlayerName.Trim();

        if (playerName.Length > MaxPlayerNameLength)
            return PlayerNameResult.Invalid($"Player name must be {MaxPlayerNameLength} characters or fewer.");

        if (playerName.Any(char.IsControl))
            return PlayerNameResult.Invalid("Player name contains invalid characters.");

        var isTaken = await db.Guests.AnyAsync(g => g.PlayerName == playerName && g.DeviceId != deviceId);
        if (isTaken)
            return PlayerNameResult.Invalid("Player name is already taken.");

        return PlayerNameResult.Valid(playerName);
    }

    private static async Task<string> GenerateUniqueDefaultPlayerNameAsync(AppDbContext db)
    {
        for (var attempt = 0; attempt < MaxPlayerNameAttempts; attempt++)
        {
            var playerName = GenerateDefaultPlayerName();
            if (!await db.Guests.AnyAsync(g => g.PlayerName == playerName))
                return playerName;
        }

        var existingDefaultNames = await db.Guests
            .Where(g => g.PlayerName.StartsWith(DefaultPlayerNamePrefix))
            .Select(g => g.PlayerName)
            .ToListAsync();
        var usedDefaultNames = existingDefaultNames.ToHashSet(StringComparer.Ordinal);

        for (var number = MinDefaultPlayerNameNumber; number < MaxDefaultPlayerNameNumberExclusive; number++)
        {
            var playerName = $"{DefaultPlayerNamePrefix}{number}";
            if (!usedDefaultNames.Contains(playerName))
                return playerName;
        }

        throw new InvalidOperationException("Could not generate a unique player name.");
    }

    private static string GenerateDefaultPlayerName()
    {
        var number = RandomNumberGenerator.GetInt32(
            MinDefaultPlayerNameNumber,
            MaxDefaultPlayerNameNumberExclusive);
        return $"{DefaultPlayerNamePrefix}{number}";
    }

    private static CookieOptions SessionCookieOptions(HttpRequest request) => new()
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

    private sealed record PlayerNameResult(bool IsValid, string Name, string Error)
    {
        public static PlayerNameResult Valid(string name) => new(true, name, "");
        public static PlayerNameResult Invalid(string error) => new(false, "", error);
    }
}
