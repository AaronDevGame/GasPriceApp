public sealed class DoePumpPriceReport
{
    public long Id { get; set; }
    public string Section { get; set; } = "";
    public string? Subdivision { get; set; }
    public DateOnly WeekStart { get; set; }
    public DateOnly WeekEnd { get; set; }
    public string SourceUrl { get; set; } = "";
    public string? ContentHash { get; set; }
    public DateTime ImportedAtUtc { get; set; }
    public int PriceRows { get; set; }
    public string Status { get; set; } = "imported";
}
