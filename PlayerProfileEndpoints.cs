using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

public record PlayerProfileResponse(long PlayerId, string PlayerName);

public static class PlayerProfileEndpoints
{
    public static void MapPlayerProfileEndpoints(
        this WebApplication app,
        AuthService authService,
        string instanceId)
    {
        app.MapPatch(ApiRoutes.PlayerProfile, async (HttpRequest request, AppDbContext db) =>
        {
            var auth = await PlayerAuthentication.AuthenticateAsync(request, db, authService);
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
            if (!TryReadPlayerName(patchDocument.RootElement, out var requestedPlayerName, out var error))
                return ApiResults.BadRequest(error, instanceId);

            var nameResult = await PlayerNameService.ValidateRequestedNameAsync(
                db,
                auth.Guest.DeviceId,
                requestedPlayerName);
            if (!nameResult.IsValid)
                return ApiResults.BadRequest(nameResult.Error, instanceId);

            auth.Guest.PlayerName = nameResult.Name;

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (
                ex.InnerException is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation
                })
            {
                return ApiResults.BadRequest("Player name is already taken.", instanceId);
            }

            return ApiResults.Ok(
                new PlayerProfileResponse(auth.Guest.PlayerId, auth.Guest.PlayerName),
                "player_profile_updated",
                instanceId);
        });
    }

    private static bool TryReadPlayerName(
        JsonElement root,
        out string playerName,
        out string error)
    {
        playerName = "";
        error = "";

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "Player profile patch must be a JSON object.";
            return false;
        }

        var hasPlayerName = false;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name != "playerName")
            {
                error = $"Unknown player profile field '{property.Name}'.";
                return false;
            }

            if (hasPlayerName)
            {
                error = "Player name must be provided only once.";
                return false;
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                error = "Player name must be a string.";
                return false;
            }

            playerName = property.Value.GetString() ?? "";
            hasPlayerName = true;
        }

        if (!hasPlayerName)
        {
            error = "Player name is required.";
            return false;
        }

        return true;
    }
}
