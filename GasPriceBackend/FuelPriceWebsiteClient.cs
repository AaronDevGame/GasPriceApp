using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record FuelPriceWebsiteSource(
    string Id, string Name, string Origin, bool Enabled, int Priority);

public sealed record FuelPriceWebsiteResult(
    JsonElement Result, string SourceId, DateTime DataAsOfUtc);

public sealed class FuelPriceWebsiteCatalog
{
    public const string RelativePath = "agents/fuel-price-sources.json";
    public IReadOnlyList<FuelPriceWebsiteSource> Sources { get; }

    public FuelPriceWebsiteCatalog(IHostEnvironment environment)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(environment.ContentRootPath, RelativePath)));
        if (!document.RootElement.TryGetProperty("sources", out var sources) ||
            sources.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Fuel-price sources must contain a sources array.");

        var entries = new List<FuelPriceWebsiteSource>();
        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object ||
                !source.TryGetProperty("id", out var idValue) ||
                !source.TryGetProperty("name", out var nameValue) ||
                !source.TryGetProperty("origin", out var originValue) ||
                !source.TryGetProperty("enabled", out var enabledValue) ||
                !source.TryGetProperty("priority", out var priorityValue) ||
                idValue.ValueKind != JsonValueKind.String ||
                nameValue.ValueKind != JsonValueKind.String ||
                originValue.ValueKind != JsonValueKind.String ||
                enabledValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !priorityValue.TryGetInt32(out var priority) || priority < 0)
                throw new InvalidDataException("Each fuel-price source requires valid id, name, origin, enabled, and priority fields.");

            var id = idValue.GetString()!;
            var name = nameValue.GetString()!;
            var origin = originValue.GetString()!;
            var expectedHost = id switch
            {
                "metrofueltracker" => "metrofueltracker.com",
                "gaswatchph" => "gaswatchph.com",
                _ => throw new InvalidDataException($"Fuel-price source '{id}' needs a parser before it can be enabled.")
            };
            if (string.IsNullOrWhiteSpace(name) ||
                !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                uri.Host != expectedHost ||
                !uri.IsDefaultPort || uri.AbsolutePath != "/" ||
                uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
                entries.Any(existing => existing.Id == id))
                throw new InvalidDataException($"Fuel-price source '{id}' has an invalid or duplicate configuration.");

            entries.Add(new FuelPriceWebsiteSource(id, name, origin.TrimEnd('/'),
                enabledValue.GetBoolean(), priority));
        }

        Sources = entries.Where(source => source.Enabled).OrderBy(source => source.Priority).ToArray();
    }

    public bool HasSourceDomain(JsonElement result, IReadOnlyList<string> scannedDomains)
    {
        if (scannedDomains.Count == 0 ||
            result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("sources", out var sources) ||
            sources.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object ||
                !source.TryGetProperty("url", out var url) ||
                url.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri))
                continue;

            if (scannedDomains.Any(domain => uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }
}

public sealed partial class FuelPriceWebsiteClient(
    HttpClient httpClient,
    FuelPriceWebsiteCatalog catalog,
    ILogger<FuelPriceWebsiteClient> logger)
{
    private const int MaxPageBytes = 2_000_000;

    public async Task<FuelPriceWebsiteResult?> FindBestAsync(
        FuelPriceSearchRequest location,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(location.City))
            return null;

        var requests = catalog.Sources.Select(source => ReadSourceAsync(
            source, location, nowUtc, cancellationToken));
        var results = await Task.WhenAll(requests);
        return results.Where(result => result is not null)
            .OrderByDescending(result => result!.DataAsOfUtc)
            .ThenBy(result => catalog.Sources.First(source => source.Id == result!.SourceId).Priority)
            .FirstOrDefault();
    }

    private async Task<FuelPriceWebsiteResult?> ReadSourceAsync(
        FuelPriceWebsiteSource source,
        FuelPriceSearchRequest location,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var slug = Slug(location.City!);
            if (slug.Length == 0)
                return null;
            var path = source.Id == "metrofueltracker" ? $"/prices/{slug}" : $"/{slug}";
            var url = source.Origin + path;
            using var response = await httpClient.GetAsync(url,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength is > MaxPageBytes)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(bytes, cancellationToken)) > 0)
            {
                if (buffer.Length + count > MaxPageBytes)
                    return null;
                await buffer.WriteAsync(bytes.AsMemory(0, count), cancellationToken);
            }

            var html = Encoding.UTF8.GetString(buffer.ToArray());
            var parsed = source.Id switch
            {
                "metrofueltracker" => ParseMetroFuel(html, location),
                "gaswatchph" => ParseGasWatch(html, location),
                _ => null
            };
            if (parsed is null || !TryFreshDate(parsed.Date, nowUtc, out var dateUtc))
                return null;

            var result = new
            {
                location = new { city = location.City, province = location.Province,
                    region = location.Region, country = "Philippines" },
                status = "city_estimate",
                source_tier = "aggregator",
                estimate_area = new { level = "city", name = location.City },
                prices = new
                {
                    diesel = Range(parsed.Diesel),
                    gasoline_91 = Range(parsed.Gasoline91),
                    gasoline_95 = Range(parsed.Gasoline95)
                },
                basis = $"City fuel prices reported by {source.Name}.",
                confidence = "medium",
                sources = new[] { new {
                    name = source.Name, url,
                    published_at = parsed.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    geographic_coverage = location.City!
                } },
                data_as_of = parsed.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            };
            return new FuelPriceWebsiteResult(
                JsonSerializer.SerializeToElement(result), source.Id, dateUtc);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Fuel-price source {SourceId} timed out.", source.Id);
            return null;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Fuel-price source {SourceId} returned an HTTP error: {StatusCode}.",
                source.Id, ex.StatusCode);
            return null;
        }
        catch (JsonException)
        {
            logger.LogWarning("Fuel-price source {SourceId} returned invalid price data.", source.Id);
            return null;
        }
        catch (InvalidOperationException)
        {
            logger.LogWarning("Fuel-price source {SourceId} returned unexpected price data.", source.Id);
            return null;
        }
        catch (IOException)
        {
            logger.LogWarning("Fuel-price source {SourceId} could not be read.", source.Id);
            return null;
        }
    }

    private sealed record ParsedPrices(
        DateOnly Date, (decimal Min, decimal Max)? Diesel,
        (decimal Min, decimal Max)? Gasoline91,
        (decimal Min, decimal Max)? Gasoline95);

    private static ParsedPrices? ParseMetroFuel(string html, FuelPriceSearchRequest location)
    {
        foreach (Match script in JsonLdScript().Matches(html))
        {
            using var document = JsonDocument.Parse(WebUtility.HtmlDecode(script.Groups[1].Value));
            if (!document.RootElement.TryGetProperty("@graph", out var graph) ||
                graph.ValueKind != JsonValueKind.Array)
                continue;

            var page = graph.EnumerateArray().FirstOrDefault(node => IsType(node, "WebPage"));
            var breadcrumb = graph.EnumerateArray().FirstOrDefault(node => IsType(node, "BreadcrumbList"));
            if (page.ValueKind != JsonValueKind.Object ||
                breadcrumb.ValueKind != JsonValueKind.Object ||
                !page.TryGetProperty("description", out var description) ||
                !breadcrumb.TryGetProperty("itemListElement", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                continue;

            var names = items.EnumerateArray().Select(item => ReadString(item, "name")).ToArray();
            if (names.Length < 4 || !SameArea(names[^1], location.City) ||
                !SameArea(names[^2], location.Province) ||
                !TryDate(description.GetString(), UpdatedDate(), out var date))
                return null;

            var prices = graph.EnumerateArray().Where(node => IsType(node, "AggregateOffer"))
                .ToDictionary(node => ReadString(node, "name") ?? "", node => OfferRange(node),
                    StringComparer.OrdinalIgnoreCase);
            prices.TryGetValue("Diesel", out var diesel);
            prices.TryGetValue("Unleaded 91", out var gasoline91);
            prices.TryGetValue("Premium 95", out var gasoline95);
            return HasPrice(diesel, gasoline91, gasoline95)
                ? new ParsedPrices(date, diesel, gasoline91, gasoline95) : null;
        }
        return null;
    }

    private static ParsedPrices? ParseGasWatch(string html, FuelPriceSearchRequest location)
    {
        var stationsMatch = StationsArray().Match(html);
        if (!stationsMatch.Success ||
            !TryDate(html, LastUpdatedDate(), out var date))
            return null;

        var areaMatched = false;
        foreach (Match script in JsonLdScript().Matches(html))
        {
            using var document = JsonDocument.Parse(WebUtility.HtmlDecode(script.Groups[1].Value));
            var root = document.RootElement;
            if (!IsType(root, "WebPage") ||
                !root.TryGetProperty("about", out var about) ||
                !about.TryGetProperty("containedInPlace", out var province))
                continue;
            areaMatched = SameArea(ReadString(about, "name"), location.City) &&
                SameArea(ReadString(province, "name"), location.Province);
            break;
        }
        if (!areaMatched)
            return null;

        using var stations = JsonDocument.Parse(stationsMatch.Groups[1].Value);
        if (stations.RootElement.ValueKind != JsonValueKind.Array)
            return null;
        var diesel = new List<decimal>();
        var gasoline91 = new List<decimal>();
        var gasoline95 = new List<decimal>();
        foreach (var station in stations.RootElement.EnumerateArray())
        {
            if (!SameArea(ReadString(station, "area"), location.City) ||
                !station.TryGetProperty("prices", out var prices))
                continue;
            AddPrice(prices, "diesel", diesel);
            AddPrice(prices, "unleaded", gasoline91);
            AddPrice(prices, "premium95", gasoline95);
        }
        if (diesel.Count + gasoline91.Count + gasoline95.Count == 0)
            return null;
        return new ParsedPrices(date, ToRange(diesel), ToRange(gasoline91), ToRange(gasoline95));
    }

    private static void AddPrice(JsonElement prices, string name, List<decimal> values)
    {
        if (prices.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDecimal(out var price) && price is >= 1 and <= 300)
            values.Add(price);
    }

    private static (decimal Min, decimal Max)? OfferRange(JsonElement offer)
    {
        if (ReadString(offer, "priceCurrency") != "PHP" ||
            !offer.TryGetProperty("lowPrice", out var low) ||
            !offer.TryGetProperty("highPrice", out var high) ||
            !decimal.TryParse(low.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var min) ||
            !decimal.TryParse(high.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var max) ||
            min < 1 || max > 300 || min > max)
            return null;
        return (min, max);
    }

    private static (decimal Min, decimal Max)? ToRange(List<decimal> prices)
        => prices.Count == 0 ? null : (prices.Min(), prices.Max());

    private static bool HasPrice(params (decimal Min, decimal Max)?[] ranges)
        => ranges.Any(range => range is not null);

    private static object Range((decimal Min, decimal Max)? value)
        => new { min_price = value?.Min, max_price = value?.Max,
            currency = "PHP", unit = "liter" };

    private static bool IsType(JsonElement value, string type)
        => value.ValueKind == JsonValueKind.Object && ReadString(value, "@type") == type;

    private static string? ReadString(JsonElement value, string property)
        => value.ValueKind == JsonValueKind.Object &&
           value.TryGetProperty(property, out var field) &&
           field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static bool SameArea(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second) &&
           Slug(first) == Slug(second);

    private static string Slug(string name)
    {
        var normalized = name.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(character) && character <= 127)
                builder.Append(char.ToLowerInvariant(character));
            else if ((char.IsWhiteSpace(character) || character == '-') &&
                     builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }
        return builder.ToString().TrimEnd('-');
    }

    private static bool TryDate(string? input, Regex pattern, out DateOnly date)
    {
        date = default;
        var match = pattern.Match(input ?? "");
        return match.Success && DateOnly.TryParseExact(match.Groups[1].Value,
            "MMMM d, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool TryFreshDate(DateOnly date, DateTime nowUtc, out DateTime dateUtc)
    {
        var philippines = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");
        dateUtc = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), philippines);
        var utcNow = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        return dateUtc <= utcNow && dateUtc >= utcNow.AddDays(-7);
    }

    [GeneratedRegex("<script[^>]*type=[\"']application/ld\\+json[\"'][^>]*>(.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex JsonLdScript();

    [GeneratedRegex("Updated (\\w+ \\d{1,2}, \\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex UpdatedDate();

    [GeneratedRegex("Last verified (\\w+ \\d{1,2}, \\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex LastUpdatedDate();

    [GeneratedRegex(@"var STATIONS\s*=\s*(\[.*?\]);", RegexOptions.Singleline)]
    private static partial Regex StationsArray();
}
