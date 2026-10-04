using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

public sealed record DoeFuelPriceItem(
    string City, string Province, string? Region, string OilCompany, string FuelGrade,
    decimal MinPricePerLiter, decimal MaxPricePerLiter, DateOnly WeekStart,
    DateOnly WeekEnd, string SourceUrl, DateTime FetchedAtUtc);

public sealed record DoeFuelPriceFeed(
    string? City, string? Province, string? Region, DateOnly? WeekStart,
    DateOnly? WeekEnd, IReadOnlyList<DoeFuelPriceItem> Prices);

public sealed record DoeFuelPriceImportResult(
    string City, string Province, DateOnly WeekStart, DateOnly WeekEnd,
    string SourceUrl, int Added, int Updated, string Status);

public sealed record ExtractedDoeFuelPrice(
    [property: JsonPropertyName("oil_company")] string OilCompany,
    [property: JsonPropertyName("fuel_grade")] string FuelGrade,
    [property: JsonPropertyName("min_price_per_liter")] decimal MinPricePerLiter,
    [property: JsonPropertyName("max_price_per_liter")] decimal MaxPricePerLiter);

public sealed record DoeFuelPriceExtraction(
    [property: JsonPropertyName("week_start")] string WeekStart,
    [property: JsonPropertyName("week_end")] string WeekEnd,
    [property: JsonPropertyName("rows")] IReadOnlyList<ExtractedDoeFuelPrice> Rows);

public sealed record DoeReportSource(string Section, string? Subdivision,
    DateOnly WeekStart, string Url);

public sealed record DoeReportListingError(string Section, string Url, string Error);

public sealed record DoeReportDiscovery(IReadOnlyList<DoeReportSource> Reports,
    IReadOnlyList<DoeReportListingError> Errors);

public sealed record ExtractedDoeBulkPrice(
    [property: JsonPropertyName("c")] string City,
    [property: JsonPropertyName("p")] string Province,
    [property: JsonPropertyName("r")] string? Region,
    [property: JsonPropertyName("o")] string OilCompany,
    [property: JsonPropertyName("g")] string FuelGrade,
    [property: JsonPropertyName("lo")] decimal MinPricePerLiter,
    [property: JsonPropertyName("hi")] decimal MaxPricePerLiter);

public sealed record DoeBulkExtraction(
    [property: JsonPropertyName("week_start")] string WeekStart,
    [property: JsonPropertyName("week_end")] string WeekEnd,
    [property: JsonPropertyName("rows")] IReadOnlyList<ExtractedDoeBulkPrice> Rows);

public sealed record DoeReportImportResult(string SourceUrl, string Status,
    int Added, int Updated, int PriceRows)
{
    public int DuplicatesIgnored { get; init; }
    public int AggregateRowsIgnored { get; init; }
}

public sealed record ValidatedDoeBulkRows(IReadOnlyList<ExtractedDoeBulkPrice> Rows,
    int DuplicatesIgnored, int AggregateRowsIgnored);

public sealed class DoeFuelPriceImporter(
    HttpClient sourceClient,
    IHttpClientFactory clients,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<DoeFuelPriceImporter> logger)
{
    private const int MaxPdfBytes = 12 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, DateTime> FailedAttempts = new();
    private static readonly ConcurrentDictionary<string, DateTime> ListingChecks = new();
    private static readonly Regex PdfLinks = new(
        "<a\\b[^>]*href\\s*=\\s*[\"'](?<url>[^\"']+\\.pdf(?:\\?[^\"']*)?)[\"'][^>]*>(?<label>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private const string ListingMonths =
        "January|February|March|April|May|June|July|August|September|Sept|Sep|October|November|December|Dec";
    // Consume the whole range so the last match in preceding text retains its
    // start date, including headings such as "September 29 to October 5".
    private static readonly Regex DateLabel = new(
        "(?<month>" + ListingMonths + ")\\s*(?<day>\\d{1,2})" +
        "(?:\\s*(?:to|[-–—])\\s*(?:(?:" + ListingMonths + ")\\s*)?\\d{1,2})?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> Grades = new(StringComparer.OrdinalIgnoreCase)
        { "RON 91", "RON 95", "RON 97", "RON 100", "DIESEL", "DIESEL PLUS", "KEROSENE" };
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object", additionalProperties = false,
        required = new[] { "week_start", "week_end", "rows" },
        properties = new
        {
            week_start = new { type = "string" },
            week_end = new { type = "string" },
            rows = new { type = "array", items = new
            {
                type = "object", additionalProperties = false,
                required = new[] { "oil_company", "fuel_grade", "min_price_per_liter", "max_price_per_liter" },
                properties = new
                {
                    oil_company = new { type = "string" }, fuel_grade = new { type = "string" },
                    min_price_per_liter = new { type = "number" },
                    max_price_per_liter = new { type = "number" }
                }
            } }
        }
    });
    private static readonly JsonElement BulkSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object", additionalProperties = false,
        required = new[] { "week_start", "week_end", "rows" },
        properties = new
        {
            week_start = new { type = "string" }, week_end = new { type = "string" },
            rows = new { type = "array", items = new
            {
                type = "object", additionalProperties = false,
                required = new[] { "c", "p", "r", "o", "g", "lo", "hi" },
                properties = new
                {
                    c = new { type = "string" }, p = new { type = "string" },
                    r = new { type = new[] { "string", "null" } },
                    o = new { type = "string" }, g = new { type = "string" },
                    lo = new { type = "number" }, hi = new { type = "number" }
                }
            } }
        }
    });

    private readonly HttpClient _openAiClient = clients.CreateClient("doe-pump-price-openai");
    private readonly string _instructions = File.ReadAllText(Path.Combine(
        environment.ContentRootPath, "agents/philippines-doe-pump-price-extractor.md"));
    private readonly string _bulkInstructions = File.ReadAllText(Path.Combine(
        environment.ContentRootPath, "agents/philippines-doe-pump-price-bulk-extractor.md"));

    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration["OPENAI_API_KEY"]);

    public async Task<IReadOnlyList<DoeFuelPrice>> GetAsync(
        AppDbContext db, ResolvedFuelLocation location, DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(location.City) || string.IsNullOrWhiteSpace(location.Province))
            return [];
        var cached = await ReadAsync(db, location, nowUtc, cancellationToken);
        var key = Normalize(location.City) + "|" + Normalize(location.Province);
        TrimAttempts(nowUtc);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc),
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila"));
        var daysSinceTuesday = ((int)localNow.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7;
        var currentTuesday = DateOnly.FromDateTime(localNow.AddDays(-daysSinceTuesday));
        if (cached.Count > 0 &&
            (cached[0].WeekStart >= currentTuesday ||
             localNow < currentTuesday.ToDateTime(new TimeOnly(6, 0))))
            return cached;
        if (!IsConfigured ||
            (FailedAttempts.TryGetValue(key, out var last) && last > nowUtc.AddMinutes(-15)) ||
            (cached.Count > 0 && ListingChecks.TryGetValue(key, out var checkedAt) &&
             checkedAt > nowUtc.AddMinutes(-15)))
            return cached;
        ListingChecks[key] = nowUtc;
        try
        {
            await ImportAsync(db, location, nowUtc, null, cancellationToken);
            var imported = await ReadAsync(db, location, nowUtc, cancellationToken);
            if (imported.Count == 0)
                FailedAttempts[key] = nowUtc;
            return imported.Count > 0 ? imported : cached;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or
            JsonException or TaskCanceledException or DbUpdateException)
        {
            FailedAttempts[key] = nowUtc;
            logger.LogWarning(ex, "DOE pump price import failed for {City}, {Province}.",
                location.City, location.Province);
            return cached;
        }
    }

    public async Task<IReadOnlyList<DoeFuelPrice>> ReadAsync(
        AppDbContext db, ResolvedFuelLocation location, DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(location.City) || string.IsNullOrWhiteSpace(location.Province))
            return [];
        if (!DoeLocationMatcher.RegionConsistent(location.Region, location.Province))
            return [];
        var provinceKeys = DoeLocationMatcher.ProvinceKeyCandidates(location.Province);
        var today = PhilippineDate(nowUtc);
        var earliest = today.AddDays(-7);
        var rows = await db.DoeFuelPrices.AsNoTracking()
            .Where(p => provinceKeys.Contains(p.ProvinceKey) &&
                p.WeekEnd >= earliest && p.WeekStart <= today)
            .OrderByDescending(p => p.WeekStart).ThenBy(p => p.OilCompany)
            .ThenBy(p => p.FuelGrade).ToListAsync(cancellationToken);
        var matched = DoeLocationMatcher.MatchCity(rows, location.City,
            location.Province, location.Region);
        var latest = matched.FirstOrDefault()?.WeekStart;
        return latest is null ? [] : matched.Where(p => p.WeekStart == latest).ToArray();
    }

    public async Task<DoeFuelPriceImportResult> ImportAsync(
        AppDbContext db, ResolvedFuelLocation location, DateTime nowUtc,
        string? requestedUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(location.City) || string.IsNullOrWhiteSpace(location.Province))
            throw new InvalidDataException("DOE import requires a city and province.");
        if (!IsConfigured)
            throw new InvalidDataException("OPENAI_API_KEY is required for DOE PDF extraction.");
        var discovered = requestedUrl is null
            ? await FindLatestPdfAsync(location, nowUtc, cancellationToken)
            : (FuelAdjustmentImporter.ValidatePdfUrl(requestedUrl), (DateOnly?)null);
        var (url, listedWeekStart) = discovered;
        var cityKeys = CityKeyCandidates(location.City);
        var provinceKeys = DoeLocationMatcher.ProvinceKeyCandidates(location.Province);
        if (requestedUrl is null && listedWeekStart.HasValue)
        {
            var saved = await db.DoeFuelPrices.AsNoTracking().Where(p =>
                provinceKeys.Contains(p.ProvinceKey) && p.WeekStart == listedWeekStart.Value &&
                p.SourceUrl == url).ToListAsync(cancellationToken);
            if (DoeLocationMatcher.MatchCity(saved, location.City,
                location.Province, location.Region).Count > 0)
                return new DoeFuelPriceImportResult(location.City, location.Province,
                    listedWeekStart.Value, listedWeekStart.Value.AddDays(6), url, 0, 0,
                    "already_imported");
        }
        var pdf = await DownloadAsync(url, cancellationToken);
        var extraction = await ExtractAsync(pdf, location, listedWeekStart, cancellationToken);
        if (!DateOnly.TryParseExact(extraction.WeekStart, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start) ||
            !DateOnly.TryParseExact(extraction.WeekEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var end) ||
            start.DayOfWeek != DayOfWeek.Tuesday || end != start.AddDays(6) ||
            (listedWeekStart.HasValue && listedWeekStart.Value != start) ||
            start > PhilippineDate(nowUtc).AddDays(1) ||
            extraction.Rows is null or { Count: < 1 or > 100 })
            throw new InvalidDataException("DOE extraction has an invalid week or no usable prices.");

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in extraction.Rows)
        {
            if (string.IsNullOrWhiteSpace(row.OilCompany) || row.OilCompany.Length > 100 ||
                new[] { "INDEPENDENT", "COMMON", "COMMON PRICE", "OVERALL", "OVERALL RANGE" }
                    .Contains(row.OilCompany.Trim().ToUpperInvariant()) ||
                !Grades.Contains(row.FuelGrade?.Trim() ?? "") ||
                row.MinPricePerLiter is < 1 or > 300 ||
                row.MaxPricePerLiter is < 1 or > 300 ||
                row.MinPricePerLiter > row.MaxPricePerLiter ||
                decimal.Round(row.MinPricePerLiter, 2) != row.MinPricePerLiter ||
                decimal.Round(row.MaxPricePerLiter, 2) != row.MaxPricePerLiter ||
                !unique.Add(row.OilCompany.Trim() + "|" + (row.FuelGrade?.Trim() ?? "")))
                throw new InvalidDataException("DOE extraction contains an invalid or duplicate company price.");
        }

        var cityKey = Normalize(location.City);
        var provinceKey = Normalize(location.Province);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var report = await db.DoePumpPriceReports.FirstOrDefaultAsync(r =>
            r.SourceUrl == url && r.WeekStart == start, cancellationToken);
        if (report is null)
        {
            report = new DoePumpPriceReport
            {
                Section = RegionSection(location) ?? "legacy",
                Subdivision = SouthSubdivision(location),
                WeekStart = start, WeekEnd = end, SourceUrl = url,
                ImportedAtUtc = nowUtc, Status = "partial"
            };
            db.DoePumpPriceReports.Add(report);
            await db.SaveChangesAsync(cancellationToken);
        }
        var provinceRows = await db.DoeFuelPrices.Where(p => p.WeekStart == start &&
            provinceKeys.Contains(p.ProvinceKey)).ToListAsync(cancellationToken);
        var existing = DoeLocationMatcher.MatchCity(provinceRows, location.City,
            location.Province, location.Region).ToList();
        var oldReportIds = new HashSet<long>();
        var added = 0;
        var updated = 0;
        foreach (var row in extraction.Rows)
        {
            var company = row.OilCompany.Trim();
            var grade = row.FuelGrade!.Trim().ToUpperInvariant();
            var match = existing.FirstOrDefault(p =>
                p.OilCompany.Equals(company, StringComparison.OrdinalIgnoreCase) && p.FuelGrade == grade);
            if (match is null)
            {
                match = new DoeFuelPrice { WeekStart = start, CityKey = cityKey,
                    ProvinceKey = provinceKey, OilCompany = company, FuelGrade = grade,
                    ReportId = report.Id };
                db.DoeFuelPrices.Add(match);
                existing.Add(match);
                added++;
            }
            else if (match.MinPricePerLiter != row.MinPricePerLiter ||
                match.MaxPricePerLiter != row.MaxPricePerLiter || match.SourceUrl != url ||
                match.WeekEnd != end)
                updated++;
            else
                continue;
            if (match.ReportId is long oldReportId && oldReportId != report.Id)
                oldReportIds.Add(oldReportId);
            match.City = location.City;
            match.Province = location.Province;
            match.Region = location.Region;
            match.WeekEnd = end;
            match.MinPricePerLiter = row.MinPricePerLiter;
            match.MaxPricePerLiter = row.MaxPricePerLiter;
            match.SourceUrl = url;
            match.ReportId = report.Id;
            match.FetchedAtUtc = nowUtc;
        }
        await db.SaveChangesAsync(cancellationToken);
        report.PriceRows = await db.DoeFuelPrices.CountAsync(p => p.ReportId == report.Id,
            cancellationToken);
        foreach (var oldReportId in oldReportIds)
        {
            var count = await db.DoeFuelPrices.CountAsync(p => p.ReportId == oldReportId,
                cancellationToken);
            await db.DoePumpPriceReports.Where(r => r.Id == oldReportId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.PriceRows, count), cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        if (added + updated > 0)
            await db.FuelPriceCaches.Where(c => c.RefreshAfter > nowUtc &&
                ((c.Scope == FuelPriceCacheScopes.City && cityKeys.Contains(c.CityKey) &&
                  c.ProvinceKey == provinceKey) ||
                 (c.Scope == FuelPriceCacheScopes.Province && c.ProvinceKey == provinceKey) ||
                 c.Scope == FuelPriceCacheScopes.Region))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.RefreshAfter, nowUtc), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DoeFuelPriceImportResult(location.City, location.Province, start, end,
            url, added, updated, added == 0 && updated == 0 ? "already_imported" :
                updated > 0 ? "updated" : "imported");
    }

    public async Task<DoeReportDiscovery> FindReportsAsync(
        DateOnly? from, DateOnly? to, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var sections = new[] { "ncr-pump-prices", "north-luzon-pump-prices",
            "south-luzon-pump-prices", "visayas-pump-prices", "mindanao-pump-prices" };
        var reports = new List<DoeReportSource>();
        var errors = new List<DoeReportListingError>();
        foreach (var section in sections)
        {
            var page = "https://doe.gov.ph/data-and-prices/liquid-fuels/retail-pump-prices/" + section;
            string html;
            try
            {
                using var response = await sourceClient.GetAsync(page, cancellationToken);
                response.EnsureSuccessStatusCode();
                html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                errors.Add(new DoeReportListingError(section, page,
                    "DOE regional listing was unavailable."));
                logger.LogWarning(ex, "DOE report listing failed for {Section}.", section);
                continue;
            }
            foreach (Match link in PdfLinks.Matches(html))
            {
                var label = Regex.Replace(link.Groups["label"].Value, "<[^>]+>", " ").Trim();
                var precedingText = Regex.Replace(html[..link.Index], "<[^>]+>", " ");
                var date = DateLabel.Match(label);
                if (!date.Success)
                {
                    var dates = DateLabel.Matches(precedingText);
                    if (dates.Count > 0) date = dates[^1];
                }
                var years = Regex.Matches(precedingText, @"(?<!\d)20\d{2}(?!\d)");
                if (!date.Success || years.Count == 0) continue;
                var month = date.Groups["month"].Value;
                if (month.Equals("Sept", StringComparison.OrdinalIgnoreCase)) month = "Sep";
                if (!DateTime.TryParseExact(month + " " + date.Groups["day"].Value + " " +
                    years[^1].Value, new[] { "MMMM d yyyy", "MMM d yyyy" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
                var start = DateOnly.FromDateTime(day);
                if ((from.HasValue && start.AddDays(7) < from.Value) ||
                    (to.HasValue && start > to.Value) ||
                    start > PhilippineDate(nowUtc).AddDays(1)) continue;
                if (!Uri.TryCreate(new Uri(page), link.Groups["url"].Value, out var uri)) continue;
                try { FuelAdjustmentImporter.ValidatePdfUrl(uri.AbsoluteUri); }
                catch (ArgumentException) { continue; }
                string? subdivision = null;
                if (section == "south-luzon-pump-prices")
                    subdivision = new[] { "Calabarzon", "Mimaropa", "Bicol" }
                        .FirstOrDefault(s => label.Contains(s, StringComparison.OrdinalIgnoreCase));
                reports.Add(new DoeReportSource(section, subdivision, start, uri.AbsoluteUri));
            }
        }
        var distinct = reports.DistinctBy(r => r.Url + "|" + r.WeekStart).ToArray();
        if (from.HasValue)
            return new DoeReportDiscovery(distinct.OrderBy(r => r.WeekStart)
                .ThenBy(r => r.Section).ThenBy(r => r.Subdivision).ToArray(), errors);
        // South Luzon subdivisions can publish at different times.
        return new DoeReportDiscovery(distinct.GroupBy(r => r.Section + "|" + r.Subdivision)
            .SelectMany(group => group.Where(r => r.WeekStart == group.Max(x => x.WeekStart)))
            .OrderBy(r => r.Section).ThenBy(r => r.Subdivision).ToArray(), errors);
    }

    public async Task<DoeReportImportResult> ImportReportAsync(AppDbContext db,
        DoeReportSource source, DateTime nowUtc, DateOnly? from, DateOnly? to,
        bool checkForCorrections,
        CancellationToken cancellationToken)
    {
        var report = await db.DoePumpPriceReports.FirstOrDefaultAsync(r =>
            r.SourceUrl == source.Url && r.WeekStart == source.WeekStart,
            cancellationToken);
        var wasComplete = report?.Status == "imported";
        if (report is { Status: "imported", ContentHash: not null } && !checkForCorrections)
            return new DoeReportImportResult(source.Url, "already_imported", 0, 0, report.PriceRows);
        var pdf = await DownloadAsync(source.Url, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(pdf));
        if (report is { Status: "imported" } && report.ContentHash == hash)
            return new DoeReportImportResult(source.Url, "already_imported", 0, 0, report.PriceRows);
        var extraction = await ExtractAllAsync(pdf, source, cancellationToken);
        if (!DateOnly.TryParseExact(extraction.WeekStart, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start) ||
            !DateOnly.TryParseExact(extraction.WeekEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var end) ||
            start != source.WeekStart || end < start || end > start.AddDays(7) ||
            extraction.Rows is null or { Count: < 1 or > 4000 })
            throw new InvalidDataException("DOE report extraction has an invalid week or no usable prices.");
        if ((from.HasValue && end < from.Value) || (to.HasValue && start > to.Value))
            return new DoeReportImportResult(source.Url, "outside_range", 0, 0, 0);
        var extractedRows = source.Section == "ncr-pump-prices"
            ? extraction.Rows.Select(row => row with
            {
                Province = "Metro Manila", Region = "National Capital Region"
            }).ToArray()
            : extraction.Rows;
        var validated = ValidateBulkRows(extractedRows);
        if (report is { Status: "imported" } && validated.Rows.Count < report.PriceRows)
            throw new InvalidDataException(
                "Corrected DOE report yielded fewer rows than its previous import; review it before replacing prices.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (report is null)
        {
            report = new DoePumpPriceReport
            {
                Section = source.Section, Subdivision = source.Subdivision,
                WeekStart = start, WeekEnd = end, SourceUrl = source.Url,
                ImportedAtUtc = nowUtc, Status = "partial"
            };
            db.DoePumpPriceReports.Add(report);
            await db.SaveChangesAsync(cancellationToken);
        }
        var existing = await db.DoeFuelPrices.Where(p => p.WeekStart == start)
            .ToListAsync(cancellationToken);
        var byKey = existing.GroupBy(p =>
            p.CityKey + "|" + p.ProvinceKey + "|" + Normalize(p.OilCompany) + "|" + p.FuelGrade)
            .ToDictionary(g => g.Key, g => g.First());
        var oldReportIds = new HashSet<long>();
        var added = 0;
        var updated = 0;
        foreach (var row in validated.Rows)
        {
            var cityKey = Normalize(row.City);
            var provinceKey = Normalize(row.Province);
            var company = row.OilCompany.Trim();
            var grade = row.FuelGrade!.Trim().ToUpperInvariant();
            var key = cityKey + "|" + provinceKey + "|" + Normalize(company) + "|" + grade;
            if (!byKey.TryGetValue(key, out var price))
            {
                price = new DoeFuelPrice { WeekStart = start, CityKey = cityKey,
                    ProvinceKey = provinceKey, OilCompany = company, FuelGrade = grade };
                db.DoeFuelPrices.Add(price);
                byKey[key] = price;
                added++;
            }
            else if (price.MinPricePerLiter != row.MinPricePerLiter ||
                price.MaxPricePerLiter != row.MaxPricePerLiter ||
                price.ReportId != report.Id || price.SourceUrl != source.Url)
                updated++;
            else continue;
            if (price.ReportId is long oldReportId && oldReportId != report.Id)
                oldReportIds.Add(oldReportId);
            price.ReportId = report.Id;
            price.City = row.City.Trim();
            price.Province = row.Province.Trim();
            price.Region = row.Region?.Trim() ??
                (source.Section == "ncr-pump-prices" ? "National Capital Region" : source.Subdivision);
            price.WeekEnd = end;
            price.MinPricePerLiter = row.MinPricePerLiter;
            price.MaxPricePerLiter = row.MaxPricePerLiter;
            price.SourceUrl = source.Url;
            price.FetchedAtUtc = nowUtc;
        }
        await db.SaveChangesAsync(cancellationToken);
        report.Section = source.Section;
        report.Subdivision = source.Subdivision;
        report.WeekEnd = end;
        report.ContentHash = hash;
        report.ImportedAtUtc = nowUtc;
        report.Status = "imported";
        report.PriceRows = await db.DoeFuelPrices.CountAsync(p => p.ReportId == report.Id,
            cancellationToken);
        foreach (var oldReportId in oldReportIds)
        {
            var count = await db.DoeFuelPrices.CountAsync(p => p.ReportId == oldReportId,
                cancellationToken);
            await db.DoePumpPriceReports.Where(r => r.Id == oldReportId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.PriceRows, count), cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        if (added + updated > 0)
        {
            var cityKeys = validated.Rows.SelectMany(r => CityKeyCandidates(r.City)).Distinct().ToArray();
            var provinceKeys = validated.Rows.SelectMany(r =>
                DoeLocationMatcher.ProvinceKeyCandidates(r.Province)).Distinct().ToArray();
            await db.FuelPriceCaches.Where(c => c.RefreshAfter > nowUtc &&
                ((c.Scope == FuelPriceCacheScopes.City && cityKeys.Contains(c.CityKey) &&
                  provinceKeys.Contains(c.ProvinceKey)) ||
                 (c.Scope == FuelPriceCacheScopes.Province && provinceKeys.Contains(c.ProvinceKey)) ||
                 c.Scope == FuelPriceCacheScopes.Region))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.RefreshAfter, nowUtc), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new DoeReportImportResult(source.Url,
            wasComplete && added + updated == 0 ? "already_imported" : "imported",
            added, updated,
            report.PriceRows)
        {
            DuplicatesIgnored = validated.DuplicatesIgnored,
            AggregateRowsIgnored = validated.AggregateRowsIgnored
        };
    }

    public static ValidatedDoeBulkRows ValidateBulkRows(
        IReadOnlyList<ExtractedDoeBulkPrice> rows)
    {
        var unique = new Dictionary<string, ExtractedDoeBulkPrice>(StringComparer.OrdinalIgnoreCase);
        var duplicatesIgnored = 0;
        var aggregatesIgnored = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (string.IsNullOrWhiteSpace(row.OilCompany))
                throw new InvalidDataException($"DOE report has no oil company at extracted row {index + 1}.");
            var company = Normalize(row.OilCompany);
            if (new[] { "INDEPENDENT", "COMMON", "COMMON PRICE", "OVERALL", "OVERALL RANGE" }
                .Contains(company))
            {
                aggregatesIgnored++;
                continue;
            }
            if (string.IsNullOrWhiteSpace(row.City) || row.City.Length > 100 ||
                string.IsNullOrWhiteSpace(row.Province) || row.Province.Length > 100 ||
                row.Region is { Length: > 100 } || row.OilCompany.Length > 100)
                throw new InvalidDataException(
                    $"DOE report has an invalid locality or company at extracted row {index + 1}.");
            var grade = (row.FuelGrade ?? "").Trim().ToUpperInvariant();
            if (!Grades.Contains(grade))
                throw new InvalidDataException(
                    $"DOE report has an unsupported fuel grade at extracted row {index + 1}: {grade[..Math.Min(grade.Length, 40)]}.");
            if (row.MinPricePerLiter is < 1 or > 300 ||
                row.MaxPricePerLiter is < 1 or > 300 ||
                row.MinPricePerLiter > row.MaxPricePerLiter ||
                decimal.Round(row.MinPricePerLiter, 2) != row.MinPricePerLiter ||
                decimal.Round(row.MaxPricePerLiter, 2) != row.MaxPricePerLiter)
                throw new InvalidDataException(
                    $"DOE report has an invalid price at extracted row {index + 1}.");
            var key = Normalize(row.City) + "|" + Normalize(row.Province) + "|" +
                company + "|" + grade;
            if (unique.TryGetValue(key, out var previous))
            {
                if (previous.MinPricePerLiter != row.MinPricePerLiter ||
                    previous.MaxPricePerLiter != row.MaxPricePerLiter)
                    throw new InvalidDataException(
                        $"DOE report has conflicting prices for {row.City.Trim()}, {row.Province.Trim()}, {row.OilCompany.Trim()}, {grade}.");
                duplicatesIgnored++;
                if (previous.Region is null && row.Region is not null)
                    unique[key] = row;
                continue;
            }
            unique.Add(key, row);
        }
        if (unique.Count == 0)
            throw new InvalidDataException("DOE report extraction has no usable company prices.");
        return new ValidatedDoeBulkRows(unique.Values.ToArray(), duplicatesIgnored,
            aggregatesIgnored);
    }

    private async Task<(string Url, DateOnly? ListedWeekStart)> FindLatestPdfAsync(ResolvedFuelLocation location, DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var section = RegionSection(location);
        if (section is null)
            throw new InvalidDataException("DOE regional report could not be determined for this location.");
        var southSubdivision = section == "south-luzon-pump-prices"
            ? SouthSubdivision(location) : null;
        if (section == "south-luzon-pump-prices" && southSubdivision is null)
            throw new InvalidDataException("DOE South Luzon subregion could not be determined.");
        var page = "https://doe.gov.ph/data-and-prices/liquid-fuels/retail-pump-prices/" + section;
        using var response = await sourceClient.GetAsync(page, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(cancellationToken));
        var candidates = new List<(DateOnly Start, string Url)>();
        foreach (Match link in PdfLinks.Matches(html))
        {
            var label = Regex.Replace(link.Groups["label"].Value, "<[^>]+>", " ");
            if (southSubdivision is not null &&
                !label.Contains(southSubdivision, StringComparison.OrdinalIgnoreCase))
                continue;
            var precedingText = Regex.Replace(html[..link.Index], "<[^>]+>", " ");
            var date = DateLabel.Match(label);
            if (!date.Success)
            {
                var precedingDates = DateLabel.Matches(precedingText);
                if (precedingDates.Count > 0)
                    date = precedingDates[^1];
            }
            if (!date.Success)
                continue;
            // Match visible page text, not URL escapes such as "%2015" in PDF links.
            var years = Regex.Matches(precedingText, @"(?<!\d)20\d{2}(?!\d)");
            if (years.Count == 0 ||
                !int.TryParse(years[^1].Value, CultureInfo.InvariantCulture, out var year))
                continue;
            var month = date.Groups["month"].Value;
            if (month.Equals("Sept", StringComparison.OrdinalIgnoreCase)) month = "Sep";
            if (!DateTime.TryParseExact(
                    month + " " +
                    date.Groups["day"].Value + " " + year.ToString(CultureInfo.InvariantCulture),
                    new[] { "MMMM d yyyy", "MMM d yyyy" }, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var start))
                continue;
            if (!Uri.TryCreate(new Uri(page), link.Groups["url"].Value, out var uri))
                continue;
            try { FuelAdjustmentImporter.ValidatePdfUrl(uri.AbsoluteUri); }
            catch (ArgumentException) { continue; }
            candidates.Add((DateOnly.FromDateTime(start), uri.AbsoluteUri));
        }
        var today = PhilippineDate(nowUtc);
        var latest = candidates.Where(c => c.Start <= today.AddDays(1))
            .OrderByDescending(c => c.Start).FirstOrDefault();
        if (latest.Url is null || latest.Start.AddDays(13) < today)
            throw new InvalidDataException("DOE has no recent regional pump-price PDF.");
        return (latest.Url, latest.Start);
    }

    private async Task<byte[]> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await sourceClient.GetAsync(url,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxPdfBytes)
            throw new InvalidDataException("DOE PDF exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + count > MaxPdfBytes)
                throw new InvalidDataException("DOE PDF exceeds the size limit.");
            buffer.Write(chunk, 0, count);
        }
        var pdf = buffer.ToArray();
        if (pdf.Length < 5 || Encoding.ASCII.GetString(pdf, 0, 5) != "%PDF-")
            throw new InvalidDataException("DOE source did not return a PDF.");
        return pdf;
    }

    private Task<DoeFuelPriceExtraction> ExtractAsync(byte[] pdf, ResolvedFuelLocation location,
        DateOnly? listedWeekStart,
        CancellationToken cancellationToken) => ExtractDocumentAsync<DoeFuelPriceExtraction>(
            pdf, _instructions,
            $"Extract DOE pump prices for city/municipality {location.City}, province {location.Province}. The DOE listing labels this PDF's week as starting {listedWeekStart?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unknown"}.",
            Schema, 6000, cancellationToken);

    private Task<DoeBulkExtraction> ExtractAllAsync(byte[] pdf, DoeReportSource source,
        CancellationToken cancellationToken) => ExtractDocumentAsync<DoeBulkExtraction>(
            pdf, _bulkInstructions,
            $"Extract all localities in this {source.Section} DOE report" +
            (source.Subdivision is null ? "" : $" ({source.Subdivision})") +
            $". The DOE listing labels the week as starting {source.WeekStart:yyyy-MM-dd}.",
            BulkSchema, 24000, cancellationToken);

    private async Task<T> ExtractDocumentAsync<T>(byte[] pdf, string instructions,
        string prompt, JsonElement schema, int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        var model = configuration["OPENAI_MODEL"];
        model = string.IsNullOrWhiteSpace(model) ? "gpt-5.6-luna" : model.Trim();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/responses")
        {
            Content = JsonContent.Create(new
            {
                model, instructions,
                input = new[] { new { role = "user", content = new object[]
                {
                    new { type = "input_text", text = prompt },
                    new { type = "input_file", filename = "doe-pump-prices.pdf",
                        file_data = "data:application/pdf;base64," + Convert.ToBase64String(pdf) }
                } } },
                text = new { format = new { type = "json_schema", name = "doe_pump_prices",
                    strict = true, schema } },
                max_output_tokens = maxOutputTokens, store = false
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration["OPENAI_API_KEY"]);
        using var response = await _openAiClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("DOE price extraction failed.", null, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("status", out var status) ||
            status.GetString() != "completed" ||
            !document.RootElement.TryGetProperty("output", out var outputs) ||
            outputs.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("DOE price extraction was incomplete.");
        var text = new StringBuilder();
        foreach (var output in outputs.EnumerateArray())
        {
            if (!output.TryGetProperty("type", out var type) || type.GetString() != "message" ||
                !output.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in content.EnumerateArray())
                if (part.TryGetProperty("type", out var partType) && partType.GetString() == "output_text")
                    text.Append(part.GetProperty("text").GetString());
        }
        if (text.Length == 0)
            throw new InvalidDataException("DOE price extraction returned no rows.");
        return JsonSerializer.Deserialize<T>(text.ToString()) ??
            throw new InvalidDataException("DOE price extraction returned invalid rows.");
    }

    public static string Normalize(string value) => string.Join(' ', value.Split(
        (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToUpperInvariant();

    private static string[] CityKeyCandidates(string city)
    {
        var exact = Normalize(city);
        var withoutMarks = new StringBuilder();
        foreach (var character in exact.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                withoutMarks.Append(character);
        var folded = withoutMarks.ToString();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { exact, folded })
        {
            var baseName = name.StartsWith("CITY OF ", StringComparison.Ordinal)
                ? name[8..]
                : name.EndsWith(" CITY", StringComparison.Ordinal)
                    ? name[..^5]
                    : name;
            names.Add(baseName);
            names.Add(baseName + " CITY");
            names.Add("CITY OF " + baseName);
        }
        return names.ToArray();
    }

    private static DateOnly PhilippineDate(DateTime utc) => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc),
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila")));

    private static void TrimAttempts(DateTime nowUtc)
    {
        foreach (var attempts in new[] { FailedAttempts, ListingChecks })
        {
            if (attempts.Count < 512) continue;
            foreach (var entry in attempts.Where(entry => entry.Value <= nowUtc.AddMinutes(-15)))
                attempts.TryRemove(entry.Key, out _);
            if (attempts.Count <= 1024) continue;
            foreach (var entry in attempts.OrderBy(entry => entry.Value).Take(attempts.Count - 512))
                attempts.TryRemove(entry.Key, out _);
        }
    }

    private static string? RegionSection(ResolvedFuelLocation location)
    {
        if (!DoeLocationMatcher.RegionConsistent(location.Region, location.Province)) return null;
        return DoeLocationMatcher.SectionForProvince(location.Province) ??
            DoeLocationMatcher.SectionForRegion(location.Region);
    }

    private static string? SouthSubdivision(ResolvedFuelLocation location)
    {
        return DoeLocationMatcher.SouthSubdivision(location.Province);
    }
}
