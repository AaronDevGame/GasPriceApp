public static class PlayerAuthentication
{
    public static async Task<PlayerAuthResult> AuthenticateAsync(
        HttpRequest request,
        AppDbContext db,
        AuthService authService)
    {
        if (!request.Headers.TryGetValue("Authorization", out var authHeader))
            return PlayerAuthResult.Invalid(AuthErrors.MissingAuthorizationHeader);

        if (!request.Headers.TryGetValue(AuthEndpoints.DeviceIdHeader, out var deviceIdHeader) ||
            string.IsNullOrWhiteSpace(deviceIdHeader.ToString()))
            return PlayerAuthResult.Invalid(AuthErrors.MissingDeviceIdHeader);

        var parts = authHeader.ToString().Split(' ', 2);
        if (parts.Length != 2 ||
            !parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(parts[1]))
            return PlayerAuthResult.Invalid(AuthErrors.InvalidPlayerToken);

        var token = parts[1].Trim();
        var deviceId = deviceIdHeader.ToString().Trim();
        var guest = await db.Guests.FindAsync(deviceId);

        if (guest is null)
            return PlayerAuthResult.Invalid(AuthErrors.InvalidPlayerCredentials);

        if (!guest.IsLoggedIn)
            return PlayerAuthResult.Invalid(AuthErrors.PlayerNotLoggedIn);

        if (guest.AccessTokenHash is not null && guest.AccessTokenExpiresAt <= DateTime.UtcNow)
            return PlayerAuthResult.Invalid(AuthErrors.AccessTokenExpired);

        return authService.IsValidAccessToken(guest, token, DateTime.UtcNow)
            ? PlayerAuthResult.Valid(guest)
            : PlayerAuthResult.Invalid(AuthErrors.InvalidPlayerCredentials);
    }
}

public sealed record PlayerAuthResult(bool IsValid, Guest? Guest, string Error)
{
    public static PlayerAuthResult Valid(Guest guest) => new(true, guest, "");
    public static PlayerAuthResult Invalid(string error) => new(false, null, error);
}
