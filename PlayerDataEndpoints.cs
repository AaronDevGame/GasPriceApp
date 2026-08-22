using System.Text.Json;

public record PlayerDataResponse(
    long PlayerId,
    int Health,
    long Money,
    JsonElement Position,
    JsonElement Inventory,
    JsonElement ExtraData,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public static class PlayerDataEndpoints
{
    public static void MapPlayerDataEndpoints(this WebApplication app, string instanceId)
    {
        app.MapGet(ApiRoutes.PlayerData, async (HttpRequest request, AppDbContext db) =>
        {
            var auth = await AuthenticatePlayerAsync(request, db);
            if (!auth.IsValid || auth.Guest is null)
                return ApiResults.Unauthorized(auth.Error, instanceId);

            var now = DateTime.UtcNow;
            var playerData = await PlayerDataStore.EnsureForGuestAsync(db, auth.Guest, now);
            await SaveIfChangedAsync(db);

            return ApiResults.Ok(ToResponse(playerData), "player_data", instanceId);
        });

        app.MapPatch(ApiRoutes.PlayerData, async (HttpRequest request, AppDbContext db) =>
        {
            var auth = await AuthenticatePlayerAsync(request, db);
            if (!auth.IsValid || auth.Guest is null)
                return ApiResults.Unauthorized(auth.Error, instanceId);

            if (!request.HasJsonContentType())
                return ApiResults.BadRequest("Request body must be JSON.", instanceId);

            JsonDocument patchDocument;
            try
            {
                patchDocument = await JsonDocument.ParseAsync(request.Body);
            }
            catch (JsonException)
            {
                return ApiResults.BadRequest("Request body must be valid JSON.", instanceId);
            }

            using var _ = patchDocument;
            var now = DateTime.UtcNow;
            var playerData = await PlayerDataStore.EnsureForGuestAsync(db, auth.Guest, now);

            if (!TryApplyPatch(playerData, patchDocument.RootElement, out var error))
                return ApiResults.BadRequest(error, instanceId);

            playerData.UpdatedAt = now;
            await db.SaveChangesAsync();

            return ApiResults.Ok(ToResponse(playerData), "player_data_updated", instanceId);
        });
    }

    private static async Task<PlayerAuthResult> AuthenticatePlayerAsync(HttpRequest request, AppDbContext db)
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

        if (guest is null || guest.Token != token)
            return PlayerAuthResult.Invalid(AuthErrors.InvalidPlayerCredentials);

        return guest.IsLoggedIn
            ? PlayerAuthResult.Valid(guest)
            : PlayerAuthResult.Invalid(AuthErrors.PlayerNotLoggedIn);
    }

    private static bool TryApplyPatch(PlayerData playerData, JsonElement root, out string error)
    {
        error = "";

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "Player data patch must be a JSON object.";
            return false;
        }

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "health":
                    if (!property.Value.TryGetInt32(out var health) || health < 0)
                    {
                        error = "Health must be a whole number greater than or equal to 0.";
                        return false;
                    }

                    playerData.Health = health;
                    break;

                case "money":
                    if (!property.Value.TryGetInt64(out var money) || money < 0)
                    {
                        error = "Money must be a whole number greater than or equal to 0.";
                        return false;
                    }

                    playerData.Money = money;
                    break;

                case "position":
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        error = "Position must be a JSON object.";
                        return false;
                    }

                    playerData.PositionJson = property.Value.GetRawText();
                    break;

                case "inventory":
                    if (property.Value.ValueKind != JsonValueKind.Array)
                    {
                        error = "Inventory must be a JSON array.";
                        return false;
                    }

                    playerData.InventoryJson = property.Value.GetRawText();
                    break;

                case "extraData":
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        error = "Extra data must be a JSON object.";
                        return false;
                    }

                    playerData.ExtraDataJson = property.Value.GetRawText();
                    break;

                default:
                    error = $"Unknown player data field '{property.Name}'.";
                    return false;
            }
        }

        return true;
    }

    private static async Task SaveIfChangedAsync(AppDbContext db)
    {
        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync();
    }

    private static PlayerDataResponse ToResponse(PlayerData playerData) =>
        new(
            playerData.PlayerId,
            playerData.Health,
            playerData.Money,
            ParseJson(playerData.PositionJson),
            ParseJson(playerData.InventoryJson),
            ParseJson(playerData.ExtraDataJson),
            playerData.CreatedAt,
            playerData.UpdatedAt);

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record PlayerAuthResult(bool IsValid, Guest? Guest, string Error)
    {
        public static PlayerAuthResult Valid(Guest guest) => new(true, guest, "");
        public static PlayerAuthResult Invalid(string error) => new(false, null, error);
    }
}
