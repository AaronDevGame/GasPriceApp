using System.Text.Json;

public sealed record FuelMapAdjustment(string Fuel, string? OilCompany, DateOnly EffectiveDate,
    DateTime EffectiveAtUtc, decimal ChangePerLiter, string[] SourceUrls);

public static class FuelMapEstimates
{
    // Expired articles remain historical evidence. Superseded articles and forecasts do not.
    public static FuelMapAdjustment[] Evidence(IEnumerable<FuelAdjustment> notices,
        IEnumerable<FuelNews> news, DateTime now)
    {
        var result = new List<FuelMapAdjustment>();
        foreach (var row in notices.Where(row => row.FetchedAtUtc <= now))
        {
            if (!Uri.TryCreate(row.SourceUrl, UriKind.Absolute, out var uri) ||
                !FuelAdjustmentImporter.IsAllowedPdfUrl(uri)) continue;
            Add("gasoline", row.GasolineChangePerLiter);
            Add("diesel", row.DieselChangePerLiter);
            void Add(string fuel, decimal? change)
            {
                if (change is >= -30 and <= 30 && !string.IsNullOrWhiteSpace(row.OilCompany))
                    result.Add(new(fuel, row.OilCompany, row.EffectiveDatePhilippines,
                        row.EffectiveAtUtc ?? StartUtc(row.EffectiveDatePhilippines).AddHours(6),
                        change.Value, [row.SourceUrl]));
            }
        }
        foreach (var row in news.Where(row => row.Category == "adjustment" && row.Status == "confirmed" &&
                     row.SupersededById is null && row.PublishedAtUtc <= now && row.UpdatedAtUtc <= now))
        {
            FuelNewsContent? article;
            try { article = JsonSerializer.Deserialize<FuelNewsContent>(row.ContentJson, FuelNewsValidation.Json); }
            catch (JsonException) { continue; }
            if (article is not { Status: "confirmed", EffectiveDatePhilippines: { } date } ||
                article.Adjustments is null || article.Sources is not { Length: > 0 } ||
                article.Sources.Any(source => source.PublishedAtUtc > now ||
                    !Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https")) continue;
            foreach (var change in article.Adjustments.Where(change => change.Status == "confirmed" &&
                         change.Fuel is "gasoline" or "diesel" && change.MinChangePerLiter == change.MaxChangePerLiter &&
                         change.MinChangePerLiter is >= -30 and <= 30))
                result.Add(new(change.Fuel, change.OilCompany, date,
                    article.EffectiveAtUtc ?? StartUtc(date).AddHours(6), change.MinChangePerLiter,
                    article.Sources.Select(source => source.Url).ToArray()));
        }
        return result.Where(item => DateOnly.FromDateTime(item.EffectiveAtUtc.AddHours(8)) == item.EffectiveDate)
            .ToArray();
    }

    public static DateTime StartUtc(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-8);

    // Adjust each company's observation before combining ranges. Never generalize a company notice.
    // Require evidence (including explicit zero changes) for every intervening weekly Tuesday.
    public static (DoeFuelPrice[] Rows, FuelMapAdjustment[] Applied)? Estimate(
        DoeFuelPrice[] rows, FuelMapAdjustment[] evidence, DateOnly today, DateTime now)
    {
        var shifted = new List<DoeFuelPrice>();
        var applied = new List<FuelMapAdjustment>();
        foreach (var row in rows)
        {
            var baseline = row.MonitoringEnd ?? row.WeekEnd;
            if (baseline < row.WeekStart || baseline > row.WeekEnd) return null;
            var fuel = row.FuelGrade == FuelMapPriceService.GasolineGrade ? "gasoline" : "diesel";
            var candidates = evidence.Where(item => item.Fuel == fuel && item.EffectiveDate > baseline &&
                item.EffectiveDate <= today && (item.OilCompany is null ||
                    DoeLocationMatcher.Normalize(item.OilCompany) == DoeLocationMatcher.Normalize(row.OilCompany))).ToArray();
            var required = new HashSet<DateOnly>(candidates.Where(item => item.EffectiveAtUtc <= now).Select(item => item.EffectiveDate));
            for (var day = baseline.AddDays(1); day <= today; day = day.AddDays(1))
                if (day.DayOfWeek == DayOfWeek.Tuesday && StartUtc(day).AddHours(6) <= now) required.Add(day);
            decimal delta = 0;
            foreach (var date in required.Order())
            {
                var events = candidates.Where(item => item.EffectiveDate == date).ToArray();
                // Conflicting amounts/times are ambiguous, even across DOE and news sources.
                if (events.Length == 0 || events.Select(item => (item.ChangePerLiter, item.EffectiveAtUtc)).Distinct().Count() != 1 ||
                    events[0].EffectiveAtUtc > now) return null;
                delta += events[0].ChangePerLiter;
                applied.Add(new(fuel, row.OilCompany, date, events[0].EffectiveAtUtc, events[0].ChangePerLiter,
                    events.SelectMany(item => item.SourceUrls).Distinct(StringComparer.Ordinal).ToArray()));
            }
            if (row.MinPricePerLiter + delta <= 0 || row.MaxPricePerLiter + delta > 999999.99m) return null;
            shifted.Add(new DoeFuelPrice { FuelGrade = row.FuelGrade,
                MinPricePerLiter = row.MinPricePerLiter + delta, MaxPricePerLiter = row.MaxPricePerLiter + delta });
        }
        return applied.Count == 0 ? null : (shifted.ToArray(), applied.DistinctBy(item =>
            (item.Fuel, item.OilCompany, item.EffectiveDate, item.ChangePerLiter, string.Join('|', item.SourceUrls))).ToArray());
    }
}
