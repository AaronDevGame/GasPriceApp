public sealed record DoeImportPageStatus(int PageNumber, string Status,
    int Attempts, int PriceRows, string? Error)
{
    public IReadOnlyList<DoePriceRowError> CellErrors { get; init; } = [];
    public int DuplicatesIgnored { get; init; }
    public int AggregateRowsIgnored { get; init; }
    public bool Cached { get; init; }
}

public sealed record DoePriceRowError(int RowNumber, string? City, string? Province,
    string? OilCompany, string? FuelGrade, string Error);

public sealed record DoeReportPageProgress(int PagesTotal, int PagesCompleted,
    int? CurrentPage, IReadOnlyList<DoeImportPageStatus> Pages)
{
    public int PagesFailed => Pages.Count(page => page.Status == "failed");
    public int RowsSkipped => Pages.Sum(page => page.CellErrors.Count);
    public int PagesCached => Pages.Count(page => page.Cached);
}
