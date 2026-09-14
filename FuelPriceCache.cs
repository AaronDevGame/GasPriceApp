public sealed class FuelPriceCache
{
    public long Id { get; set; }
    public string Scope { get; set; } = "";
    public string? City { get; set; }
    public string Province { get; set; } = "";
    public string? CityKey { get; set; }
    public string ProvinceKey { get; set; } = "";
    public string ResultJson { get; set; } = "{}";
    public string Model { get; set; } = "";
    public DateTime CachedAt { get; set; }
    public DateTime RefreshAfter { get; set; }
}

public static class FuelPriceCacheScopes
{
    public const string City = "city";
    public const string Province = "province";
}
