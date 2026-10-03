public sealed class DoeFuelPrice
{
    public long Id { get; set; }
    public long? ReportId { get; set; }
    public DateOnly WeekStart { get; set; }
    public DateOnly WeekEnd { get; set; }
    public string City { get; set; } = "";
    public string CityKey { get; set; } = "";
    public string Province { get; set; } = "";
    public string ProvinceKey { get; set; } = "";
    public string? Region { get; set; }
    public string OilCompany { get; set; } = "";
    public string FuelGrade { get; set; } = "";
    public decimal MinPricePerLiter { get; set; }
    public decimal MaxPricePerLiter { get; set; }
    public string SourceUrl { get; set; } = "";
    public DateTime FetchedAtUtc { get; set; }
}
