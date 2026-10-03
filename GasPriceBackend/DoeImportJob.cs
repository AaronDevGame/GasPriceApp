public sealed class DoeImportJob
{
    public Guid Id { get; set; }
    public string Mode { get; set; } = "latest";
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string Status { get; set; } = "queued";
    public int? ActiveSlot { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? HeartbeatAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public int ReportsFound { get; set; }
    public int ReportsImported { get; set; }
    public int ReportsSkipped { get; set; }
    public int ReportsFailed { get; set; }
    public int PriceRowsAdded { get; set; }
    public int PriceRowsUpdated { get; set; }
    public string DetailsJson { get; set; } = "[]";
    public string? Error { get; set; }
}
