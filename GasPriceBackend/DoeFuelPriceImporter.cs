using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    private static readonly Regex DateLabel = new(
        "(?<month>January|February|March|April|May|June|July|August|September|Sept|Sep|October|November|December|Dec)\\s*(?<day>\\d{1,2})",
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

    private readonly HttpClient _openAiClient = clients.CreateClient("doe-pump-price-openai");
    private readonly string _instructions = File.ReadAllText(Path.Combine(
        environment.ContentRootPath, "agents/philippines-doe-pump-price-extractor.md"));

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
            (ListingChecks.TryGetValue(key, out var checkedAt) && checkedAt > nowUtc.AddMinutes(-15)))
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
        var cityKey = Normalize(location.City);
        var provinceKey = Normalize(location.Province);
        var today = PhilippineDate(nowUtc);
        var earliest = today.AddDays(-7);
        var rows = await db.DoeFuelPrices.AsNoTracking()
            .Where(p => p.CityKey == cityKey && p.ProvinceKey == provinceKey &&
                p.WeekEnd >= earliest && p.WeekStart <= today)
            .OrderByDescending(p => p.WeekStart).ThenBy(p => p.OilCompany)
            .ThenBy(p => p.FuelGrade).ToListAsync(cancellationToken);
        var latest = rows.FirstOrDefault()?.WeekStart;
        return latest is null ? [] : rows.Where(p => p.WeekStart == latest).ToArray();
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
        if (requestedUrl is null && listedWeekStart.HasValue &&
            await db.DoeFuelPrices.AsNoTracking().AnyAsync(p =>
                p.CityKey == Normalize(location.City) &&
                p.ProvinceKey == Normalize(location.Province) &&
                p.WeekStart == listedWeekStart.Value && p.SourceUrl == url, cancellationToken))
        {
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
        var existing = await db.DoeFuelPrices.Where(p => p.WeekStart == start &&
            p.CityKey == cityKey && p.ProvinceKey == provinceKey).ToListAsync(cancellationToken);
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
                    ProvinceKey = provinceKey, OilCompany = company, FuelGrade = grade };
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
            match.City = location.City;
            match.Province = location.Province;
            match.Region = location.Region;
            match.WeekEnd = end;
            match.MinPricePerLiter = row.MinPricePerLiter;
            match.MaxPricePerLiter = row.MaxPricePerLiter;
            match.SourceUrl = url;
            match.FetchedAtUtc = nowUtc;
        }
        await db.SaveChangesAsync(cancellationToken);
        return new DoeFuelPriceImportResult(location.City, location.Province, start, end,
            url, added, updated, added == 0 && updated == 0 ? "already_imported" :
                updated > 0 ? "updated" : "imported");
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
            var date = DateLabel.Match(label);
            if (!date.Success)
            {
                var precedingText = Regex.Replace(html[..link.Index], "<[^>]+>", " ");
                var precedingDates = DateLabel.Matches(precedingText);
                if (precedingDates.Count > 0)
                    date = precedingDates[^1];
            }
            if (!date.Success)
                continue;
            var years = Regex.Matches(html[..link.Index], @"(?<!\d)20\d{2}(?!\d)");
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

    private async Task<DoeFuelPriceExtraction> ExtractAsync(byte[] pdf, ResolvedFuelLocation location,
        DateOnly? listedWeekStart,
        CancellationToken cancellationToken)
    {
        var model = configuration["OPENAI_MODEL"];
        model = string.IsNullOrWhiteSpace(model) ? "gpt-5.6-luna" : model.Trim();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/responses")
        {
            Content = JsonContent.Create(new
            {
                model, instructions = _instructions,
                input = new[] { new { role = "user", content = new object[]
                {
                    new { type = "input_text", text = $"Extract DOE pump prices for city/municipality {location.City}, province {location.Province}. The DOE listing labels this PDF's week as starting {listedWeekStart?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unknown"}." },
                    new { type = "input_file", filename = "doe-pump-prices.pdf",
                        file_data = "data:application/pdf;base64," + Convert.ToBase64String(pdf) }
                } } },
                text = new { format = new { type = "json_schema", name = "doe_pump_prices",
                    strict = true, schema = Schema } },
                max_output_tokens = 6000, store = false
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
        return JsonSerializer.Deserialize<DoeFuelPriceExtraction>(text.ToString()) ??
            throw new InvalidDataException("DOE price extraction returned invalid rows.");
    }

    public static string Normalize(string value) => string.Join(' ', value.Split(
        (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToUpperInvariant();

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
        var value = (location.Region ?? location.Province ?? "").ToUpperInvariant();
        if (value.Contains("NATIONAL CAPITAL") || value.Contains("METRO MANILA") || value == "NCR")
            return "ncr-pump-prices";
        if (new[] { "ILOCOS", "CAGAYAN VALLEY", "CENTRAL LUZON", "CORDILLERA" }
            .Any(value.Contains) || value == "CAR" ||
            Regex.IsMatch(value, @"\bREGION (?:I|II|III)\b")) return "north-luzon-pump-prices";
        if (new[] { "CALABARZON", "MIMAROPA", "BICOL" }.Any(value.Contains) ||
            Regex.IsMatch(value, @"\bREGION (?:IV|V)\b")) return "south-luzon-pump-prices";
        if (new[] { "VISAYAS", "NEGROS ISLAND" }.Any(value.Contains) || value == "NIR" ||
            Regex.IsMatch(value, @"\bREGION (?:VI|VII|VIII)\b")) return "visayas-pump-prices";
        if (new[] { "MINDANAO", "CARAGA", "BANGSAMORO", "SOCCSKSARGEN", "DAVAO", "ZAMBOANGA" }
            .Any(value.Contains) || value == "BARMM" ||
            Regex.IsMatch(value, @"\bREGION (?:IX|X|XI|XII|XIII)\b")) return "mindanao-pump-prices";
        if (SouthSubdivision(location) is not null) return "south-luzon-pump-prices";
        return null;
    }

    private static string? SouthSubdivision(ResolvedFuelLocation location)
    {
        var region = (location.Region ?? "").ToUpperInvariant();
        var province = (location.Province ?? "").ToUpperInvariant();
        if (region.Contains("CALABARZON") || region.Contains("IV-A") || region.Contains("IV - A") ||
            new[] { "CAVITE", "LAGUNA", "BATANGAS", "RIZAL", "QUEZON" }.Contains(province))
            return "Calabarzon";
        if (region.Contains("MIMAROPA") || region.Contains("IV-B") || region.Contains("IV - B") ||
            new[] { "MARINDUQUE", "OCCIDENTAL MINDORO", "ORIENTAL MINDORO", "PALAWAN", "ROMBLON" }.Contains(province))
            return "Mimaropa";
        if (region.Contains("BICOL") || region.Contains("REGION V") ||
            new[] { "ALBAY", "CAMARINES NORTE", "CAMARINES SUR", "CATANDUANES", "MASBATE", "SORSOGON" }.Contains(province))
            return "Bicol";
        return null;
    }
}
