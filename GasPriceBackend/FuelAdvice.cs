using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

[JsonConverter(typeof(JsonStringEnumConverter<FuelAdviceStatus>))]
public enum FuelAdviceStatus
{
    [JsonStringEnumMemberName("gas_now")] GasNow,
    [JsonStringEnumMemberName("wait")] Wait,
    [JsonStringEnumMemberName("neutral")] Neutral,
    [JsonStringEnumMemberName("unknown")] Unknown
}

public sealed record FuelAdviceEvidence(
    string ConfirmationStatus, DateOnly EffectiveDatePhilippines, DateTime? EffectiveAtUtc,
    decimal MinChangePerLiter, decimal MaxChangePerLiter, string? OilCompany,
    Guid? NewsId, long? AdjustmentId, IReadOnlyList<string> SourceUrls, DateTime ValidUntilUtc);

public sealed record FuelAdviceResponse(
    string FuelType, string? OilCompany, FuelAdviceStatus Status, string Reason,
    string Message, DateTime EvaluatedAtUtc, DateTime? ValidUntilUtc,
    IReadOnlyList<FuelAdviceEvidence> Evidence, FuelAdviceEvidence? Forecast);

public sealed class FuelAdviceService(AppDbContext db, TimeProvider clock)
{
    public async Task<FuelAdviceResponse> GetAsync(string fuelType, string? oilCompany, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now.AddHours(8));
        var from = today.AddDays(-7);
        var to = today.AddDays(7);
        var evidence = new List<FuelAdviceEvidence>();
        // News already contains validated structured changes; never infer amounts from prose.
        var news = await db.FuelNews.AsNoTracking()
            .Where(n => n.Category == "adjustment" && n.ExpiresAtUtc > now.AddDays(-7) && n.SupersededById == null)
            .ToListAsync(ct);
        foreach (var item in news)
        {
            var article = JsonSerializer.Deserialize<FuelNewsContent>(item.ContentJson, FuelNewsValidation.Json)!;
            if (article.EffectiveDatePhilippines is not { } date || date < from || date > to) continue;
            foreach (var change in article.Adjustments.Where(c => c.Fuel == fuelType &&
                         (c.OilCompany is null || (oilCompany is not null &&
                          c.OilCompany.Equals(oilCompany, StringComparison.OrdinalIgnoreCase)))))
            {
                // A confirmed row within a forecast article remains forecast evidence.
                var status = article.Status == "confirmed" && change.Status == "confirmed" ? "confirmed" : "forecast";
                evidence.Add(new(status, date, article.EffectiveAtUtc,
                    change.MinChangePerLiter, change.MaxChangePerLiter, change.OilCompany,
                    item.Id, null, article.Sources.Select(s => s.Url).ToArray(), item.ExpiresAtUtc));
            }
        }

        // DOE notices are company-specific confirmed adjustments. Do not generalize one company's prices.
        if (oilCompany is not null)
        {
            var company = oilCompany.ToLowerInvariant();
            var rows = await db.FuelAdjustments.AsNoTracking()
                .Where(a => a.EffectiveDatePhilippines >= from && a.EffectiveDatePhilippines <= to &&
                            a.OilCompany.ToLower() == company).ToListAsync(ct);
            foreach (var row in rows)
            {
                var amount = fuelType switch
                {
                    "gasoline" => row.GasolineChangePerLiter,
                    "diesel" => row.DieselChangePerLiter,
                    _ => row.KeroseneChangePerLiter
                };
                if (amount is null) continue;
                evidence.Add(new("confirmed", row.EffectiveDatePhilippines, row.EffectiveAtUtc,
                    amount.Value, amount.Value, row.OilCompany, null, row.Id, [row.SourceUrl],
                    FuelAdviceCalculator.StartUtc(row.EffectiveDatePhilippines).AddDays(8)));
            }
        }
        return FuelAdviceCalculator.Calculate(fuelType, oilCompany, now, evidence);
    }
}

public static class FuelAdviceCalculator
{
    public static DateTime StartUtc(DateOnly date) =>
        date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-8);

    public static FuelAdviceResponse Calculate(string fuelType, string? oilCompany, DateTime now,
        IEnumerable<FuelAdviceEvidence> evidence)
    {
        // Hard expiration applies even if an article has a longer editorial lifetime.
        var bounded = evidence.Select(e => e with
            { ValidUntilUtc = Min(e.ValidUntilUtc, (e.EffectiveAtUtc ?? StartUtc(e.EffectiveDatePhilippines)).AddDays(7)) })
            .Where(e => StartUtc(e.EffectiveDatePhilippines) >= StartUtc(DateOnly.FromDateTime(now.AddHours(8)).AddDays(-7)) &&
                        StartUtc(e.EffectiveDatePhilippines) <= now.AddDays(7))
            .ToArray();
        var active = bounded.Where(e => e.ValidUntilUtc > now).ToArray();
        var forecast = active.Where(e => e.ConfirmationStatus == "forecast" &&
                (e.EffectiveAtUtc ?? StartUtc(e.EffectiveDatePhilippines).AddDays(1)) > now)
            .OrderBy(e => e.EffectiveDatePhilippines).ThenBy(e => e.NewsId).FirstOrDefault();
        if (forecast is not null)
            forecast = forecast with { ValidUntilUtc = Min(forecast.ValidUntilUtc,
                forecast.EffectiveAtUtc ?? StartUtc(forecast.EffectiveDatePhilippines).AddDays(1)) };
        // Expired newer events block falling back to an older, misleading rollback.
        var groups = bounded.Where(e => e.ConfirmationStatus == "confirmed")
            .GroupBy(e => e.EffectiveDatePhilippines).OrderBy(g => g.Key).ToArray();
        // The nearest upcoming change controls buying advice; otherwise use the latest applied change.
        // A date-only change is pending until the entire Philippine date has elapsed.
        var selected = groups.FirstOrDefault(g => g.Any(e =>
                (e.EffectiveAtUtc ?? StartUtc(e.EffectiveDatePhilippines).AddDays(1)) > now))
            ?? groups.LastOrDefault();
        var rows = selected?.Where(e => e.ValidUntilUtc > now)
            .OrderBy(e => e.NewsId).ThenBy(e => e.AdjustmentId).ToArray() ?? [];
        var expiry = rows.Length == 0 ? (DateTime?)null : rows.Min(e => e.ValidUntilUtc);
        FuelAdviceResponse Result(FuelAdviceStatus status, string reason, string message, DateTime? until = null) =>
            new(fuelType, oilCompany, status, reason, message, now, until ?? expiry, rows, forecast);

        if (rows.Length == 0)
            return Result(FuelAdviceStatus.Unknown, forecast is null ? "no_confirmed_adjustment" : "forecast_only",
                forecast is null ? "No current confirmed fuel-price adjustment is available for this selection."
                    : "A fuel-price adjustment is forecast, subject to change. No confirmed advice is available.",
                forecast?.ValidUntilUtc);
        if (rows.Any(e => e.MinChangePerLiter != e.MaxChangePerLiter) ||
            rows.Select(e => e.MinChangePerLiter).Distinct().Count() != 1 ||
            rows.Where(e => e.EffectiveAtUtc.HasValue).Select(e => e.EffectiveAtUtc).Distinct().Count() > 1)
            return Result(FuelAdviceStatus.Unknown, "conflicting_adjustments",
                "Confirmed sources disagree on the amount or effective time. Check the announcements.");

        var dateStart = StartUtc(rows[0].EffectiveDatePhilippines);
        var hasUnknownTime = rows.Any(e => e.EffectiveAtUtc is null);
        if (hasUnknownTime && now >= dateStart && now < dateStart.AddDays(1))
            return Result(FuelAdviceStatus.Unknown, "effective_time_unknown",
                "The adjustment is scheduled for today, but its effective time is unconfirmed.",
                Min(expiry!.Value, dateStart.AddDays(1)));
        var effective = hasUnknownTime ? dateStart.AddDays(1) : rows[0].EffectiveAtUtc!.Value;
        var upcoming = now < effective;
        var until = upcoming ? Min(expiry!.Value, hasUnknownTime ? dateStart : effective) : expiry!.Value;
        var amount = rows[0].MinChangePerLiter;
        if (amount < 0)
            return upcoming
                ? Result(FuelAdviceStatus.Wait, "rollback_upcoming", "A confirmed rollback is coming. Wait if you have enough fuel.", until)
                : Result(FuelAdviceStatus.GasNow, "rollback_effective", "The confirmed rollback is now effective. Good time to refuel.", until);
        if (amount > 0)
            return upcoming
                ? Result(FuelAdviceStatus.GasNow, "increase_upcoming", "A confirmed price increase is coming. Refuel before it takes effect.", until)
                : Result(FuelAdviceStatus.Neutral, "increase_effective", "The price increase is already effective. No clear price advantage to refueling now.", until);
        return Result(FuelAdviceStatus.Neutral, "no_price_change", "No fuel-price change is confirmed for this adjustment.", until);
    }

    private static DateTime Min(DateTime left, DateTime right) => left < right ? left : right;
}
