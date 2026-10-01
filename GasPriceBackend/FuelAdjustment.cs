public sealed class FuelAdjustment
{
    public long Id { get; set; }
    public DateOnly WeekStart { get; set; }
    public DateOnly WeekEnd { get; set; }
    public string OilCompany { get; set; } = "";
    public DateTime EffectiveAtUtc { get; set; }
    public decimal? GasolineChangePerLiter { get; set; }
    public decimal? DieselChangePerLiter { get; set; }
    public decimal? KeroseneChangePerLiter { get; set; }
    public string SourceUrl { get; set; } = "";
    public DateTime FetchedAtUtc { get; set; }
}
