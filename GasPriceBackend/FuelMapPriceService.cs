using Microsoft.EntityFrameworkCore;

public sealed record FuelMapRange(decimal MinPrice, decimal MaxPrice, string Currency = "PHP", string Unit = "liter");
public sealed record FuelMapCityPrices(string CityId, string Scope, string Status, string GasolineGrade,
    FuelMapRange? Gasoline, FuelMapRange? Diesel, DateOnly? WeekStart, DateOnly? WeekEnd,
    DateOnly? MonitoringStart, DateOnly? MonitoringEnd, string[] SourceUrls, DateTime? FetchedAtUtc,
    DateTime ExpiresAtUtc);
public sealed record FuelMapPriceFeed(string BoundaryVersion, string GasolineGrade, string DieselGrade,
    DateTime GeneratedAtUtc, DateTime ExpiresAtUtc, string FreshnessPolicy, IReadOnlyList<FuelMapCityPrices> Items);

public sealed class FuelMapPriceService(AppDbContext db, FuelMapGeography geography, TimeProvider clock)
{
    public const string GasolineGrade = "RON 91";
    public const string DieselGrade = "DIESEL";
    public const int MaxCities = 2000;
    public const string FreshnessPolicy = "doe-week-end-within-7-days-v1";

    public async Task<FuelMapPriceFeed> ReadAsync(IReadOnlyList<string> cityIds, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = PhilippineDate(now);
        // One bounded, no-tracking query for candidate province keys and two exact grades.
        // Geography.Resolve also checks actual province/name identity; no province fallback.
        var keys = cityIds.Select(id => geography.Cities[id])
            .SelectMany(city => DoeLocationMatcher.ProvinceKeyCandidates(
                city.RegionId == "1300000000" ? "Metro Manila" : city.GroupName))
            .ToHashSet(StringComparer.Ordinal);
        if (keys.Any(key => key.Contains("MAGUINDANAO", StringComparison.Ordinal))) keys.UnionWith(DoeLocationMatcher.ProvinceKeyCandidates("Maguindanao"));
        var rows = await db.DoeFuelPrices.AsNoTracking().Where(row => keys.Contains(row.ProvinceKey) &&
            (row.FuelGrade == GasolineGrade || row.FuelGrade == DieselGrade) &&
            (row.City != "" || row.CityKey != "") && row.WeekStart <= today && row.WeekEnd >= today.AddDays(-7))
            .ToListAsync(cancellationToken);
        return BuildFeed(cityIds, rows, geography, now);
    }

    public static FuelMapPriceFeed BuildFeed(IReadOnlyList<string> cityIds, IEnumerable<DoeFuelPrice> rows,
        FuelMapGeography geography, DateTime now)
    {
        var today = PhilippineDate(now);
        var wanted = cityIds.ToHashSet(StringComparer.Ordinal);
        var eligible = rows.Where(row => row.WeekStart <= today && row.WeekEnd >= today.AddDays(-7) &&
                row.WeekEnd >= row.WeekStart && row.WeekEnd <= row.WeekStart.AddDays(7) &&
                row.FetchedAtUtc <= now && row.MinPricePerLiter > 0 && row.MaxPricePerLiter >= row.MinPricePerLiter &&
                row.MaxPricePerLiter <= 999999.99m &&
                row.FuelGrade is GasolineGrade or DieselGrade && !string.IsNullOrWhiteSpace(row.OilCompany) &&
                Uri.TryCreate(row.SourceUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                uri.UserInfo.Length == 0 && (uri.Host == "doe.gov.ph" || uri.Host.EndsWith(".doe.gov.ph", StringComparison.OrdinalIgnoreCase)))
            .Select(row => (Row: row, City: geography.Resolve(row)))
            .Where(item => item.City is not null && wanted.Contains(item.City.Id))
            .GroupBy(item => item.City!.Id).ToDictionary(group => group.Key, group => group.Select(item => item.Row).ToArray());
        var items = cityIds.Select(id =>
        {
            var expiry = now.AddMinutes(5);
            if (!eligible.TryGetValue(id, out var candidates))
                return new FuelMapCityPrices(id, "city", "unavailable", GasolineGrade, null, null,
                    null, null, null, null, [], null, expiry);
            // Choose a complete report identity; never mix source reports or reporting periods.
            var report = candidates.GroupBy(row => new { row.WeekStart, row.WeekEnd, row.SourceUrl, row.ReportId })
                .OrderByDescending(group => group.Key.WeekStart).ThenByDescending(group => group.Key.WeekEnd)
                .ThenByDescending(group => group.Max(row => row.FetchedAtUtc))
                .ThenBy(group => group.Key.SourceUrl, StringComparer.Ordinal).First();
            var selected = report.GroupBy(row => DoeLocationMatcher.Normalize(row.OilCompany) + "|" + row.FuelGrade)
                .Select(group => group.OrderByDescending(row => row.FetchedAtUtc)
                    .ThenByDescending(row => row.Id).First()).ToArray();
            FuelMapRange? Range(string grade)
            {
                var values = selected.Where(row => row.FuelGrade == grade).ToArray();
                return values.Length == 0 ? null : new(values.Min(row => row.MinPricePerLiter), values.Max(row => row.MaxPricePerLiter));
            }
            var gasoline = Range(GasolineGrade);
            var diesel = Range(DieselGrade);
            // Eligibility ends at the Philippine midnight after WeekEnd + 7 days.
            var eligibleUntil = report.Key.WeekEnd.AddDays(8).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-8);
            expiry = new[] { expiry, eligibleUntil }.Min();
            DateOnly? Common(Func<DoeFuelPrice, DateOnly?> field)
            {
                var values = selected.Select(field).Distinct().ToArray();
                return values.Length == 1 ? values[0] : null;
            }
            return new FuelMapCityPrices(id, "city", gasoline is not null && diesel is not null ? "available" : "partial",
                GasolineGrade, gasoline, diesel, report.Key.WeekStart, report.Key.WeekEnd,
                Common(row => row.MonitoringStart), Common(row => row.MonitoringEnd),
                [report.Key.SourceUrl], selected.Max(row => row.FetchedAtUtc), expiry);
        }).ToArray();
        return new(FuelMapGeography.Version, GasolineGrade, DieselGrade, now,
            items.Min(item => item.ExpiresAtUtc), FreshnessPolicy, items);
    }

    private static DateOnly PhilippineDate(DateTime now) => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila")));
}
