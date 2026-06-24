public class PlayerData
{
    public string DeviceId { get; set; } = "";
    public long PlayerId { get; set; }
    public int Health { get; set; } = PlayerDataDefaults.Health;
    public long Money { get; set; } = PlayerDataDefaults.Money;
    public string PositionJson { get; set; } = PlayerDataDefaults.PositionJson;
    public string InventoryJson { get; set; } = PlayerDataDefaults.InventoryJson;
    public string ExtraDataJson { get; set; } = PlayerDataDefaults.ExtraDataJson;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class PlayerDataDefaults
{
    public const int Health = 100;
    public const long Money = 0;
    public const string PositionJson = "{}";
    public const string InventoryJson = "[]";
    public const string ExtraDataJson = "{}";

    public static PlayerData Create(Guest guest, DateTime now) => new()
    {
        DeviceId = guest.DeviceId,
        PlayerId = guest.PlayerId,
        Health = Health,
        Money = Money,
        PositionJson = PositionJson,
        InventoryJson = InventoryJson,
        ExtraDataJson = ExtraDataJson,
        CreatedAt = now,
        UpdatedAt = now
    };
}

public static class PlayerDataStore
{
    public static async Task<PlayerData> EnsureForGuestAsync(AppDbContext db, Guest guest, DateTime now)
    {
        var playerData = await db.PlayerData.FindAsync(guest.DeviceId);
        if (playerData is null)
        {
            playerData = PlayerDataDefaults.Create(guest, now);
            db.PlayerData.Add(playerData);
            return playerData;
        }

        if (playerData.PlayerId != guest.PlayerId)
        {
            playerData.PlayerId = guest.PlayerId;
            playerData.UpdatedAt = now;
        }

        return playerData;
    }
}
