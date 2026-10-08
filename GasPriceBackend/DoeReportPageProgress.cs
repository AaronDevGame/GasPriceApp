public sealed record DoeImportPageStatus(int PageNumber, string Status,
    int Attempts, int PriceRows, string? Error);

public sealed record DoeReportPageProgress(int PagesTotal, int PagesCompleted,
    int? CurrentPage, IReadOnlyList<DoeImportPageStatus> Pages)
{
    public int PagesFailed => Pages.Count(page => page.Status == "failed");
}
