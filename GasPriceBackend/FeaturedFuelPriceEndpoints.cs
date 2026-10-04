using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;

public sealed record FeaturedFuelPriceItem(
    string City,
    string Province,
    DateOnly ReportWeekStart,
    DateOnly ReportWeekEnd,
    string DataAsOf,
    FuelPriceRanges Prices,
    FuelPriceSource Source);

public sealed record FeaturedFuelPriceGroup(
    string Name,
    IReadOnlyList<FeaturedFuelPriceItem> Items);

public sealed record FeaturedFuelPriceResponse(
    IReadOnlyList<FeaturedFuelPriceGroup> Groups);

public static class FeaturedFuelPriceEndpoints
{
    private sealed record Candidate(string City, string[] Aliases);
    private sealed record Island(string Name, Candidate[] Cities);

    private static readonly Island[] Islands =
    [
        new("Luzon",
        [
            new("Quezon City", ["Quezon City", "City of Quezon"]),
            new("Baguio City", ["Baguio City", "City of Baguio", "Baguio"]),
            new("Dasmariñas", ["Dasmariñas", "Dasmariñas City", "City of Dasmariñas"])
        ]),
        new("Visayas",
        [
            new("Iloilo City", ["Iloilo City", "City of Iloilo", "Iloilo"]),
            new("Cebu City", ["Cebu City", "City of Cebu", "Cebu"]),
            new("Tacloban City", ["Tacloban City", "City of Tacloban", "Tacloban"])
        ]),
        new("Mindanao",
        [
            new("Zamboanga City", ["Zamboanga City", "City of Zamboanga", "Zamboanga"]),
            new("Cagayan de Oro City", ["Cagayan de Oro City", "City of Cagayan de Oro", "Cagayan de Oro"]),
            new("Davao City", ["Davao City", "City of Davao", "Davao"])
        ])
    ];

    public static void MapFeaturedFuelPriceEndpoints(
        this WebApplication app, AuthService authService, string instanceId)
    {
        app.MapGet(ApiRoutes.FeaturedFuelPrices, async (
            HttpRequest request, AppDbContext db, TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var auth = await PlayerAuthentication.AuthenticateAsync(request, db, authService);
            if (!auth.IsValid || auth.Guest is null)
                return auth.IsBadRequest
                    ? ApiResults.BadRequest(auth.Error, instanceId)
                    : ApiResults.Unauthorized(auth.Error, instanceId);
            if (request.Query.Count != 0)
                return ApiResults.BadRequest("Featured fuel prices do not accept query parameters.", instanceId);

            var nowUtc = timeProvider.GetUtcNow();
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
                nowUtc, TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila")).DateTime);
            var earliest = today.AddDays(-7);
            var rows = await db.DoeFuelPrices.AsNoTracking()
                .Where(p => p.WeekEnd >= earliest && p.WeekStart <= today &&
                    (p.FuelGrade == "DIESEL" || p.FuelGrade == "RON 91" ||
                     p.FuelGrade == "RON 95"))
                .ToListAsync(cancellationToken);

            return ApiResults.Ok(BuildFeed(rows, today), "featured_fuel_prices", instanceId);
        });
    }

    public static FeaturedFuelPriceResponse BuildFeed(
        IReadOnlyList<DoeFuelPrice> rows, DateOnly today)
    {
        var eligible = rows.Where(row =>
            row.WeekEnd >= today.AddDays(-7) && row.WeekStart <= today &&
            row.WeekEnd >= row.WeekStart && row.WeekEnd <= row.WeekStart.AddDays(7) &&
            row.FuelGrade is ("DIESEL" or "RON 91" or "RON 95") &&
            row.MinPricePerLiter >= 0 && row.MaxPricePerLiter >= row.MinPricePerLiter &&
            !string.IsNullOrWhiteSpace(row.SourceUrl))
            .GroupBy(row => NormalizeCity(row.City))
            .ToDictionary(group => group.Key, group => group.ToArray());

        return new FeaturedFuelPriceResponse(Islands.Select(island =>
            new FeaturedFuelPriceGroup(island.Name, island.Cities
                .Select(candidate => BuildItem(candidate, eligible, today))
                .OfType<FeaturedFuelPriceItem>()
                .ToArray())).ToArray());
    }

    private static FeaturedFuelPriceItem? BuildItem(
        Candidate candidate, Dictionary<string, DoeFuelPrice[]> eligible, DateOnly today)
    {
        var report = candidate.Aliases.Select(NormalizeCity).Distinct()
            .Where(eligible.ContainsKey)
            .SelectMany(key => eligible[key])
            .GroupBy(row => new { row.WeekStart, row.WeekEnd,
                row.ProvinceKey, row.SourceUrl })
            .OrderByDescending(group => group.Key.WeekStart)
            .ThenByDescending(group => group.Key.WeekEnd)
            .ThenByDescending(group => group.Select(row => row.FuelGrade).Distinct().Count())
            .FirstOrDefault();
        if (report is null) return null;
        var reportRows = report.ToArray();
        var latest = reportRows[0];
        if (string.IsNullOrWhiteSpace(latest.Province)) return null;

        var asOf = latest.WeekEnd <= today ? latest.WeekEnd : latest.WeekStart;
        return new FeaturedFuelPriceItem(
            candidate.City, latest.Province, latest.WeekStart, latest.WeekEnd,
            asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            new FuelPriceRanges(Range(reportRows, "DIESEL"),
                Range(reportRows, "RON 91"), Range(reportRows, "RON 95")),
            new FuelPriceSource("Philippine Department of Energy", latest.SourceUrl,
                null, candidate.City));
    }

    private static FuelPriceRange Range(IReadOnlyList<DoeFuelPrice> rows, string grade)
    {
        var matching = rows.Where(row => row.FuelGrade == grade).ToArray();
        return new FuelPriceRange(
            matching.Length == 0 ? null : (double)matching.Min(row => row.MinPricePerLiter),
            matching.Length == 0 ? null : (double)matching.Max(row => row.MaxPricePerLiter),
            "PHP", "liter");
    }

    private static string NormalizeCity(string city)
    {
        var withoutMarks = new StringBuilder();
        foreach (var character in city.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                withoutMarks.Append(character);
        return DoeFuelPriceImporter.Normalize(withoutMarks.ToString());
    }
}
