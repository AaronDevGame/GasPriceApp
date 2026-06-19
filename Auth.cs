using System.Security.Cryptography;
using System.Text;

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
    public string Token { get; init; } = "";
    public string TokenType { get; init; } = "guest";
}

public static class AuthEndpoints
{
    public const string DeviceIdHeader = "X-Device-Id";
    public const string DeviceIdCookie = "device_id";
    public const string SessionCookie = "session_active";

    public static void MapAuthEndpoints(this WebApplication app, AuthService auth, string instanceId)
    {
        app.MapPost("/login", (HttpRequest request, HttpResponse response) =>
        {
            var deviceId = ResolveDeviceId(request, response);
            var token = auth.GenerateToken(deviceId);

            // "Already logged in" = an active session from a previous /login that
            // hasn't been ended by /logout. The device_id (and therefore the token)
            // persists across logout; only this session marker is cleared.
            var alreadyLoggedIn = request.Cookies.ContainsKey(SessionCookie);

            response.Cookies.Append(SessionCookie, "1", SessionCookieOptions(request));

            return ApiResults.Ok(
                new AuthResult { DeviceId = deviceId, Token = token },
                alreadyLoggedIn ? "already_logged_in" : "guest_login",
                instanceId);
        });

        // Ends the session marker so the next /login reports guest_login again.
        // The device_id cookie is intentionally kept, so re-login yields the same token.
        app.MapPost("/logout", (HttpResponse response) =>
        {
            response.Cookies.Delete(SessionCookie);
            return ApiResults.Ok<object?>(null, "logged_out", instanceId);
        });
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

    private static CookieOptions SessionCookieOptions(HttpRequest request) => new()
    {
        HttpOnly = true,
        Secure = request.IsHttps,   // HTTPS in production (Render); still works on http locally
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromDays(365),
        IsEssential = true
    };
}
