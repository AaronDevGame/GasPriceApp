using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

public sealed record FuelPriceArea(
    string Level,
    string Name,
    string? City,
    string? Province,
    string? Region);

public sealed record FuelPriceRange(
    double? MinPrice,
    double? MaxPrice,
    string Currency,
    string Unit);

public sealed record FuelPriceRanges(
    FuelPriceRange Diesel,
    FuelPriceRange Gasoline91,
    FuelPriceRange Gasoline95);

public sealed record FuelPriceSource(
    string Name,
    string Url,
    string? PublishedAt,
    string GeographicCoverage);

public sealed record FuelPriceFeedItem(
    FuelPriceArea Area,
    bool IsLocal,
    string DataAsOf,
    string Freshness,
    FuelPriceRanges Prices,
    string SourceTier,
    string Confidence,
    IReadOnlyList<FuelPriceSource> Sources);

public sealed record FuelPriceFeedResponse(
    int Limit,
    string LocalAreaStatus,
    IReadOnlyList<FuelPriceFeedItem> Items);

public sealed record FuelPriceHistoryPoint(
    string DataAsOf,
    string Freshness,
    FuelPriceRanges Prices,
    string SourceTier,
    string Confidence,
    IReadOnlyList<FuelPriceSource> Sources);

public sealed record FuelPriceHistoryResponse(
    FuelPriceArea Area,
    int Limit,
    IReadOnlyList<FuelPriceHistoryPoint> Items);

public static class FuelPriceReadEndpoints
{
    private sealed record AreaQuery(string? City, string? Province, string? Region);

    private sealed record Snapshot(
        FuelPriceCache Cache,
        FuelPriceArea Area,
        DateTime DataAsOfUtc,
        string DataAsOf,
        FuelPriceRanges Prices,
        string SourceTier,
        string Confidence,
        IReadOnlyList<FuelPriceSource> Sources);

    public static void MapFuelPriceReadEndpoints(
        this WebApplication app,
        AuthService authService,
        string instanceId)
    {
        app.MapGet(ApiRoutes.FuelPrices, async (
            HttpRequest request,
            AppDbContext db,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var auth = await PlayerAuthentication.AuthenticateAsync(request, db, authService);
            if (!auth.IsValid || auth.Guest is null)
                return auth.IsBadRequest
                    ? ApiResults.BadRequest(auth.Error, instanceId)
                    : ApiResults.Unauthorized(auth.Error, instanceId);

            if (!TryReadQuery(request, false, out var area, out _, out var limit, out var error))
                return ApiResults.BadRequest(error, instanceId);

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var snapshots = (await db.FuelPriceCaches
                    .AsNoTracking()
                    .ToListAsync(cancellationToken))
                .Select(cache => TryReadSnapshot(cache, now))
                .OfType<Snapshot>()
                .GroupBy(snapshot => AreaKey(snapshot.Cache))
                .Select(group => group
                    .OrderByDescending(snapshot => snapshot.DataAsOfUtc)
                    .ThenByDescending(snapshot => snapshot.Cache.CachedAt)
                    .First())
                .OrderByDescending(snapshot => snapshot.DataAsOfUtc)
                .ThenByDescending(snapshot => snapshot.Cache.CachedAt)
                .ToList();

            Snapshot? local = null;
            if (area is not null)
            {
                // Prefer a current estimate at the most specific available level.
                // An old city estimate must not outrank a current province estimate.
                foreach (var currentOnly in new[] { true, false })
                {
                    foreach (var scope in new[]
                    {
                        FuelPriceCacheScopes.City,
                        FuelPriceCacheScopes.Province,
                        FuelPriceCacheScopes.Region
                    })
                    {
                        local = snapshots.FirstOrDefault(snapshot =>
                            Matches(snapshot.Cache, area, scope) &&
                            (!currentOnly || Freshness(snapshot, now) == "current"));
                        if (local is not null)
                            break;
                    }

                    if (local is not null)
                        break;
                }
            }

            var ordered = local is null
                ? snapshots
                : snapshots.Where(snapshot => AreaKey(snapshot.Cache) != AreaKey(local.Cache))
                    .Prepend(local)
                    .ToList();

            return ApiResults.Ok(
                new FuelPriceFeedResponse(
                    limit,
                    area is null ? "not_provided" : local is null ? "not_cached" : "available",
                    ordered.Take(limit)
                        .Select(snapshot => ToFeedItem(snapshot, snapshot == local, now))
                        .ToList()),
                "fuel_prices",
                instanceId);
        });

        app.MapGet(ApiRoutes.FuelPriceHistory, async (
            HttpRequest request,
            AppDbContext db,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var auth = await PlayerAuthentication.AuthenticateAsync(request, db, authService);
            if (!auth.IsValid || auth.Guest is null)
                return auth.IsBadRequest
                    ? ApiResults.BadRequest(auth.Error, instanceId)
                    : ApiResults.Unauthorized(auth.Error, instanceId);

            if (!TryReadQuery(request, true, out var area, out var scope, out var limit, out var error))
                return ApiResults.BadRequest(error, instanceId);

            var cityKey = Normalize(area!.City);
            var provinceKey = Normalize(area.Province);
            var regionKey = Normalize(area.Region);
            var candidates = await db.FuelPriceCaches
                .AsNoTracking()
                .Where(cache => cache.Scope == scope &&
                    (scope != FuelPriceCacheScopes.City || cache.CityKey == cityKey) &&
                    (scope == FuelPriceCacheScopes.Region || provinceKey == null ||
                     cache.ProvinceKey == provinceKey) &&
                    (scope != FuelPriceCacheScopes.Region || cache.RegionKey == regionKey) &&
                    (regionKey == null || cache.RegionKey == regionKey))
                .ToListAsync(cancellationToken);

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var snapshots = candidates
                .Where(cache => Matches(cache, area, scope!))
                .Select(cache => TryReadSnapshot(cache, now))
                .OfType<Snapshot>()
                .OrderByDescending(snapshot => snapshot.DataAsOfUtc)
                .ThenByDescending(snapshot => snapshot.Cache.CachedAt)
                .GroupBy(snapshot => snapshot.DataAsOf)
                .Select(group => group.First())
                .Take(limit)
                .ToList();

            if (snapshots.Count == 0)
                return ApiResults.Ok(
                    new FuelPriceHistoryResponse(
                        RequestedArea(scope!, area),
                        limit,
                        []),
                    "fuel_price_history",
                    instanceId);

            return ApiResults.Ok(
                new FuelPriceHistoryResponse(
                    snapshots[0].Area,
                    limit,
                    snapshots.Select(snapshot => new FuelPriceHistoryPoint(
                        snapshot.DataAsOf,
                        Freshness(snapshot, now),
                        snapshot.Prices,
                        snapshot.SourceTier,
                        snapshot.Confidence,
                        snapshot.Sources)).ToList()),
                "fuel_price_history",
                instanceId);
        });
    }

    private static bool TryReadQuery(
        HttpRequest request,
        bool history,
        out AreaQuery? area,
        out string? scope,
        out int limit,
        out string error)
    {
        area = null;
        scope = null;
        limit = history ? 30 : 10;
        error = "";

        foreach (var parameter in request.Query)
        {
            if (parameter.Key is not ("limit" or "city" or "province" or "region" or "scope") ||
                (parameter.Key == "scope" && !history) ||
                parameter.Value.Count != 1)
            {
                error = $"Unknown or repeated fuel-price query parameter '{parameter.Key}'.";
                return false;
            }
        }

        if (request.Query.TryGetValue("limit", out var requestedLimit) &&
            (!int.TryParse(requestedLimit, NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
             limit < 1 || limit > (history ? 100 : 10)))
        {
            error = history ? "Limit must be between 1 and 100." : "Limit must be between 1 and 10.";
            return false;
        }

        if (!TryReadAreaPart(request, "city", out var city) ||
            !TryReadAreaPart(request, "province", out var province) ||
            !TryReadAreaPart(request, "region", out var region))
        {
            error = "City, province, and region must be nonempty names of at most 100 characters.";
            return false;
        }

        if (city is not null || province is not null || region is not null)
            area = new AreaQuery(city, province, region);

        if (!history)
            return true;

        scope = request.Query["scope"].ToString();
        if (scope is not (FuelPriceCacheScopes.City or FuelPriceCacheScopes.Province or FuelPriceCacheScopes.Region))
        {
            error = "Scope must be city, province, or region.";
            return false;
        }

        if (area is null ||
            (scope == FuelPriceCacheScopes.City && (city is null ||
                (province is null && region is null))) ||
            (scope == FuelPriceCacheScopes.Province && province is null) ||
            (scope == FuelPriceCacheScopes.Region && region is null))
        {
            error = "The selected scope requires its area name; city history also requires a province or region.";
            return false;
        }

        return true;
    }

    private static bool TryReadAreaPart(HttpRequest request, string key, out string? value)
    {
        value = null;
        if (!request.Query.TryGetValue(key, out var raw))
            return true;

        value = raw.ToString().Trim();
        return value.Length is > 0 and <= 100;
    }

    private static string? Normalize(string? value)
        => value is null ? null :
            string.Join(' ', value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToUpperInvariant();

    private static bool Matches(FuelPriceCache cache, AreaQuery area, string scope)
    {
        if (cache.Scope != scope)
            return false;

        var cityKey = Normalize(area.City);
        var provinceKey = Normalize(area.Province);
        var regionKey = Normalize(area.Region);
        return scope switch
        {
            FuelPriceCacheScopes.City => cityKey is not null &&
                cache.CityKey == cityKey &&
                (provinceKey is null || cache.ProvinceKey == provinceKey) &&
                (regionKey is null || cache.RegionKey == regionKey),
            FuelPriceCacheScopes.Province => provinceKey is not null &&
                cache.ProvinceKey == provinceKey &&
                (regionKey is null || cache.RegionKey == regionKey),
            FuelPriceCacheScopes.Region => regionKey is not null &&
                cache.RegionKey == regionKey,
            _ => false
        };
    }

    private static string AreaKey(FuelPriceCache cache) => cache.Scope switch
    {
        FuelPriceCacheScopes.City => $"city\u001f{cache.RegionKey}\u001f{cache.ProvinceKey}\u001f{cache.CityKey}",
        FuelPriceCacheScopes.Province => $"province\u001f{cache.RegionKey}\u001f{cache.ProvinceKey}",
        _ => $"region\u001f{cache.RegionKey}"
    };

    private static FuelPriceArea RequestedArea(string scope, AreaQuery area)
        => new(scope, scope switch
        {
            FuelPriceCacheScopes.City => area.City!,
            FuelPriceCacheScopes.Province => area.Province!,
            _ => area.Region!
        }, area.City, area.Province, area.Region);

    private static Snapshot? TryReadSnapshot(FuelPriceCache cache, DateTime now)
    {
        try
        {
            using var document = JsonDocument.Parse(cache.ResultJson);
            var root = document.RootElement;
            // Reject the whole snapshot: its ranges may combine multiple sources.
            if (FuelPriceSourcePolicy.HasExcludedSource(root))
                return null;

            if (!root.TryGetProperty("data_as_of", out var asOfValue) ||
                asOfValue.ValueKind != JsonValueKind.String ||
                !TryParseDataAsOf(asOfValue.GetString(), out var dataAsOfUtc) ||
                dataAsOfUtc > now ||
                !root.TryGetProperty("prices", out var prices) ||
                !TryReadPrices(prices, out var ranges))
                return null;

            var areaName = cache.Scope switch
            {
                FuelPriceCacheScopes.City => cache.City,
                FuelPriceCacheScopes.Province => cache.Province,
                FuelPriceCacheScopes.Region => cache.Region,
                _ => null
            };
            if (string.IsNullOrWhiteSpace(areaName))
                return null;

            var sources = new List<FuelPriceSource>();
            if (root.TryGetProperty("sources", out var sourceArray) &&
                sourceArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var source in sourceArray.EnumerateArray())
                {
                    if (source.ValueKind != JsonValueKind.Object)
                        continue;
                    var name = ReadString(source, "name");
                    var url = ReadString(source, "url");
                    if (name is not null && url is not null)
                        sources.Add(new FuelPriceSource(name, url,
                            ReadString(source, "published_at"),
                            ReadString(source, "geographic_coverage") ?? ""));
                }
            }

            return new Snapshot(
                cache,
                new FuelPriceArea(cache.Scope, areaName, cache.City, cache.Province, cache.Region),
                dataAsOfUtc,
                asOfValue.GetString()!,
                ranges,
                ReadString(root, "source_tier") ?? "unavailable",
                ReadString(root, "confidence") ?? "none",
                sources);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadPrices(JsonElement prices, out FuelPriceRanges ranges)
    {
        ranges = default!;
        if (prices.ValueKind != JsonValueKind.Object ||
            !TryReadRange(prices, "diesel", out var diesel) ||
            !TryReadRange(prices, "gasoline_91", out var gasoline91) ||
            !TryReadRange(prices, "gasoline_95", out var gasoline95) ||
            (diesel.MinPrice is null && gasoline91.MinPrice is null && gasoline95.MinPrice is null))
            return false;

        ranges = new FuelPriceRanges(diesel, gasoline91, gasoline95);
        return true;
    }

    private static bool TryReadRange(JsonElement prices, string name, out FuelPriceRange range)
    {
        range = default!;
        if (!prices.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Object ||
            !TryReadNullableNumber(value, "min_price", out var minimum) ||
            !TryReadNullableNumber(value, "max_price", out var maximum))
            return false;

        range = new FuelPriceRange(
            minimum,
            maximum,
            ReadString(value, "currency") ?? "PHP",
            ReadString(value, "unit") ?? "liter");
        return true;
    }

    private static bool TryReadNullableNumber(JsonElement value, string name, out double? number)
    {
        number = null;
        if (!value.TryGetProperty(name, out var property))
            return false;
        if (property.ValueKind == JsonValueKind.Null)
            return true;
        if (property.ValueKind != JsonValueKind.Number ||
            !property.TryGetDouble(out var parsed) || !double.IsFinite(parsed))
            return false;
        number = parsed;
        return true;
    }

    private static string? ReadString(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryParseDataAsOf(string? value, out DateTime utc)
    {
        utc = default;
        if (value is null)
            return false;
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            utc = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue),
                TimeSpan.FromHours(8)).UtcDateTime;
            return true;
        }
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
            return false;
        utc = timestamp.UtcDateTime;
        return true;
    }

    private static string Freshness(Snapshot snapshot, DateTime now)
        => snapshot.DataAsOfUtc >= now.AddDays(-7) ? "current" : "stale";

    private static FuelPriceFeedItem ToFeedItem(Snapshot snapshot, bool isLocal, DateTime now)
        => new(snapshot.Area, isLocal, snapshot.DataAsOf,
            Freshness(snapshot, now), snapshot.Prices, snapshot.SourceTier,
            snapshot.Confidence, snapshot.Sources);
}
