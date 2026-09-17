using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

public static class PlayerNameService
{
    private const int MaxPlayerNameLength = 24;
    private const int MinDefaultPlayerNameNumber = 1000;
    private const int MaxDefaultPlayerNameNumberExclusive = 10000;
    private const int MaxPlayerNameAttempts = 20;
    private const string DefaultPlayerNamePrefix = "Player ";

    public static async Task<PlayerNameResult> ResolveInitialNameAsync(
        AppDbContext db,
        string appInstanceId,
        string? requestedPlayerName)
    {
        if (!string.IsNullOrWhiteSpace(requestedPlayerName))
            return await ValidateRequestedNameAsync(db, appInstanceId, requestedPlayerName);

        return PlayerNameResult.Valid(await GenerateUniqueDefaultNameAsync(db));
    }

    public static async Task<PlayerNameResult> ValidateRequestedNameAsync(
        AppDbContext db,
        string appInstanceId,
        string requestedPlayerName)
    {
        var playerName = requestedPlayerName.Trim();

        if (playerName.Length == 0)
            return PlayerNameResult.Invalid("Player name is required.");

        if (playerName.Length > MaxPlayerNameLength)
            return PlayerNameResult.Invalid($"Player name must be {MaxPlayerNameLength} characters or fewer.");

        if (playerName.Any(char.IsControl))
            return PlayerNameResult.Invalid("Player name contains invalid characters.");

        var isTaken = await db.Guests.AnyAsync(g =>
            g.PlayerName == playerName && g.AppInstanceId != appInstanceId);
        if (isTaken)
            return PlayerNameResult.Invalid("Player name is already taken.");

        return PlayerNameResult.Valid(playerName);
    }

    private static async Task<string> GenerateUniqueDefaultNameAsync(AppDbContext db)
    {
        for (var attempt = 0; attempt < MaxPlayerNameAttempts; attempt++)
        {
            var playerName = GenerateDefaultName();
            if (!await db.Guests.AnyAsync(g => g.PlayerName == playerName))
                return playerName;
        }

        var existingDefaultNames = await db.Guests
            .Where(g => g.PlayerName.StartsWith(DefaultPlayerNamePrefix))
            .Select(g => g.PlayerName)
            .ToListAsync();
        var usedDefaultNames = existingDefaultNames.ToHashSet(StringComparer.Ordinal);

        for (var number = MinDefaultPlayerNameNumber;
             number < MaxDefaultPlayerNameNumberExclusive;
             number++)
        {
            var playerName = $"{DefaultPlayerNamePrefix}{number}";
            if (!usedDefaultNames.Contains(playerName))
                return playerName;
        }

        throw new InvalidOperationException("Could not generate a unique player name.");
    }

    private static string GenerateDefaultName()
    {
        var number = RandomNumberGenerator.GetInt32(
            MinDefaultPlayerNameNumber,
            MaxDefaultPlayerNameNumberExclusive);
        return $"{DefaultPlayerNamePrefix}{number}";
    }
}

public sealed record PlayerNameResult(bool IsValid, string Name, string Error)
{
    public static PlayerNameResult Valid(string name) => new(true, name, "");
    public static PlayerNameResult Invalid(string error) => new(false, "", error);
}
