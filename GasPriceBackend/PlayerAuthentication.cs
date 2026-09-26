public static class PlayerAuthentication
{
    public static async Task<PlayerAuthResult> AuthenticateAsync(
        HttpRequest request,
        AppDbContext db,
        AuthService authService)
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        string token;
        string appInstanceId;
        // Explicit bearer requests never fall back to ambient cookies.
        if (request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            if (!request.Headers.TryGetValue(AuthEndpoints.AppInstanceIdHeader, out var idHeader))
                return PlayerAuthResult.Unauthorized(AuthErrors.MissingAppInstanceIdHeader);
            if (!AuthEndpoints.TryNormalizeAppInstanceId(idHeader.ToString(), out appInstanceId))
                return PlayerAuthResult.BadRequest(AuthErrors.InvalidAppInstanceId);
            var parts = authHeader.ToString().Split(' ', 2);
            if (parts.Length != 2 || !parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(parts[1]))
                return PlayerAuthResult.Unauthorized(AuthErrors.InvalidPlayerToken);
            token = parts[1].Trim();
        }
        else
        {
            if (!request.Cookies.TryGetValue(BrowserAuthentication.AccessCookie, out var accessToken))
                return PlayerAuthResult.Unauthorized(AuthErrors.MissingAuthorizationHeader);
            if (!AuthEndpoints.TryNormalizeAppInstanceId(
                    request.Cookies[AuthEndpoints.AppInstanceIdCookie], out appInstanceId))
                return PlayerAuthResult.BadRequest(AuthErrors.InvalidAppInstanceId);
            if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) &&
                !await BrowserAuthentication.ValidateCsrfAsync(request))
                return PlayerAuthResult.BadRequest(AuthErrors.InvalidCsrfToken);
            token = accessToken;
        }
        var guest = await db.Guests.FindAsync(appInstanceId);

        if (guest is null)
            return PlayerAuthResult.Unauthorized(AuthErrors.InvalidPlayerCredentials);

        if (!guest.IsLoggedIn)
            return PlayerAuthResult.Unauthorized(AuthErrors.PlayerNotLoggedIn);

        if (guest.AccessTokenHash is not null && guest.AccessTokenExpiresAt <= DateTime.UtcNow)
            return PlayerAuthResult.Unauthorized(AuthErrors.AccessTokenExpired);

        return authService.IsValidAccessToken(guest, token, DateTime.UtcNow)
            ? PlayerAuthResult.Valid(guest)
            : PlayerAuthResult.Unauthorized(AuthErrors.InvalidPlayerCredentials);
    }
}

public sealed record PlayerAuthResult(bool IsValid, Guest? Guest, string Error, bool IsBadRequest)
{
    public static PlayerAuthResult Valid(Guest guest) => new(true, guest, "", false);
    public static PlayerAuthResult Unauthorized(string error) => new(false, null, error, false);
    public static PlayerAuthResult BadRequest(string error) => new(false, null, error, true);
}
