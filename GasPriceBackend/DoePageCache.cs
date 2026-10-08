public sealed class DoePageCache
{
    public string ContentHash { get; set; } = "";
    public string ExtractorVersion { get; set; } = "";
    public int PageNumber { get; set; }
    public int PageCount { get; set; }
    public string ExtractionJson { get; set; } = "";
    public DateTime ExtractedAtUtc { get; set; }
}
