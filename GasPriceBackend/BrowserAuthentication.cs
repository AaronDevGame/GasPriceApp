using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

// Auth secrets never appear in browser response bodies. The CSRF request token is
// separate from authentication and is only useful with its HttpOnly companion cookie.
public record BrowserSessionResult(string State, string? PlayerName = null, long? PlayerId = null);

public static class BrowserAuthentication
{
    public const string AccessCookie = "__Host-gasprice_access";
    public const string GuestCookie = "__Secure-gasprice_guest";
    public const string CsrfHeader = "X-CSRF-Token";
    private static readonly TimeSpan GuestCookieLifetime = TimeSpan.FromDays(365);

    public static void AddBrowserAuthentication(this IServiceCollection services)
    {
        services.AddAntiforgery(options =>
        {
            options.HeaderName = CsrfHeader;
            options.Cookie.Name = "__Host-gasprice_csrf";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
        });
    }

    public static async Task<bool> ValidateCsrfAsync(HttpRequest request)
    {
        // This deployment intentionally supports same-origin browser traffic only.
        // Preserve the public Host and HTTPS scheme when using a reverse proxy.
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (!request.IsHttps ||
            (!string.IsNullOrEmpty(site) && site != "same-origin" && site != "none"))
            return false;
        if (request.Headers.TryGetValue("Origin", out var origin) &&
            !string.Equals(origin.ToString(), $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            await request.HttpContext.RequestServices.GetRequiredService<IAntiforgery>()
                .ValidateRequestAsync(request.HttpContext);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    public static bool TryResolveAppInstanceId(HttpRequest request, bool startNewGuest, out string id)
    {
        if (!startNewGuest && request.Cookies.TryGetValue(AuthEndpoints.AppInstanceIdCookie, out var saved))
            return AuthEndpoints.TryNormalizeAppInstanceId(saved, out id);
        id = Guid.NewGuid().ToString("D");
        return true;
    }

    public static void SetSessionCookies(HttpResponse response, string id, string accessToken,
        DateTime expiresAt, string guestCredential)
    {
        response.Cookies.Append(AuthEndpoints.AppInstanceIdCookie, id, Options("/", GuestCookieLifetime));
        response.Cookies.Append(GuestCookie, guestCredential, Options(ApiRoutes.BrowserAuthRoot, GuestCookieLifetime));
        response.Cookies.Append(AccessCookie, accessToken, Options("/", expiresAt - DateTime.UtcNow));
    }

    private static CookieOptions Options(string path, TimeSpan? maxAge = null) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = path,
        MaxAge = maxAge,
        IsEssential = true
    };

    public static void MapBrowserAuthEndpoints(this WebApplication app, AuthService auth, string instanceId)
    {
        app.MapGet(ApiRoutes.BrowserCsrf, (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps)
                return ApiResults.BadRequest("browser_https_required", instanceId);
            var tokens = antiforgery.GetAndStoreTokens(context);
            return ApiResults.Ok(new { csrfToken = tokens.RequestToken }, "csrf_token", instanceId);
        });

        app.MapPost(ApiRoutes.BrowserLogout, async (HttpRequest request, HttpResponse response, AppDbContext db) =>
        {
            response.Headers.CacheControl = "no-store";
            if (!await ValidateCsrfAsync(request))
                return ApiResults.BadRequest(AuthErrors.InvalidCsrfToken, instanceId);
            if (!AuthEndpoints.TryNormalizeAppInstanceId(
                    request.Cookies[AuthEndpoints.AppInstanceIdCookie], out var id))
                return ApiResults.BadRequest(AuthErrors.InvalidAppInstanceId, instanceId);
            var guest = await db.Guests.FindAsync(id);
            var access = request.Cookies[AccessCookie];
            var credential = request.Cookies[GuestCookie];
            // Allow explicit logout after access expiry without renewing the session.
            var validAccess = guest is not null && access is not null &&
                auth.IsValidAccessToken(guest, access, DateTime.UtcNow);
            var validCredential = guest?.GuestCredentialHash is { } hash && credential is not null &&
                auth.VerifySecret(credential, hash);
            if (guest is null || (!validAccess && !validCredential))
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
            response.Cookies.Delete(AccessCookie, Options("/"));
            // Keep the long-lived credential for explicit re-login. Status never
            // auto-resumes an account whose persisted IsLoggedIn flag is false.
            return ApiResults.Ok(new LogoutResult(false), "logged_out", instanceId);
        });

        app.MapGet(ApiRoutes.BrowserStatus, async (HttpRequest request, AppDbContext db) =>
        {
            request.HttpContext.Response.Headers.CacheControl = "no-store";
            // Browser routes only accept cookies, never credentials supplied by JS.
            if (request.Headers.ContainsKey("Authorization"))
                return ApiResults.BadRequest("browser_cookie_auth_required", instanceId);
            var result = await PlayerAuthentication.AuthenticateAsync(request, db, auth);
            if (result.IsValid && result.Guest is { } player)
                return ApiResults.Ok(new BrowserSessionResult("authenticated", player.PlayerName, player.PlayerId),
                    "auth_status", instanceId);

            var hasId = request.Cookies.TryGetValue(AuthEndpoints.AppInstanceIdCookie, out var id);
            if (!hasId)
                return ApiResults.Ok(new BrowserSessionResult("new"), "auth_status", instanceId);
            if (!AuthEndpoints.TryNormalizeAppInstanceId(id, out var normalizedId))
                return ApiResults.Ok(new BrowserSessionResult("unavailable"), "auth_status", instanceId);
            var guest = await db.Guests.AsNoTracking().FirstOrDefaultAsync(g => g.AppInstanceId == normalizedId);
            var credential = request.Cookies[GuestCookie];
            var canResume = guest?.GuestCredentialHash is { } hash && credential is not null &&
                auth.VerifySecret(credential, hash);
            // Do not expose account details or resume eligibility based on the public ID alone.
            var state = canResume ? (guest!.IsLoggedIn ? "resumable" : "signedOut") : "unavailable";
            return ApiResults.Ok(new BrowserSessionResult(state), "auth_status", instanceId);
        });
    }
}
