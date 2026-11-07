using System.Reflection;
using System.Text.Json;

internal static class ProvincePriceChecks
{
    public static void Run()
    {
        var now = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);
        var province = new ExtractedDoeBulkPrice("", "Marinduque", "Mimaropa", "TOTAL", "DIESEL", 97.10m, 98.17m);
        var validated = DoeFuelPriceImporter.ValidateBulkRows([province, province with { City = "  " },
            province with { City = "Boac", OilCompany = "PETRON" }]);
        Check(validated.Rows.Count == 2 && validated.DuplicatesIgnored == 1 &&
            validated.Rows.Count(row => row.City == "") == 1, "Canonical province identity must deduplicate and retain named-city rows.");
        foreach (var invalid in new[] { province with { Province = "" }, province with { Province = " " }, province with { Province = "---" },
            province with { City = "---" },
            province with { MinPricePerLiter = 0 }, province with { City = new string('a', 101) } })
        {
            try { DoeFuelPriceImporter.ValidateBulkRows([invalid]); throw new Exception("Invalid province/price/locality was accepted."); }
            catch (InvalidDataException) { }
        }
        try
        {
            DoeFuelPriceImporter.ValidateBulkRows([province, province with { MaxPricePerLiter = 99 }]);
            throw new Exception("Conflicting provincial company prices were accepted.");
        }
        catch (InvalidDataException) { }
        Check(DoeFuelPriceImporter.ValidateBulkRows([], allowEmpty: true).Rows.Count == 0,
            "Readable all-blank/No LFRO sections must remain empty without invalid rows.");

        DoeFuelPrice Row(string city, string provinceName, int startDay, decimal price = 95) => new()
        {
            City = city, CityKey = DoeFuelPriceImporter.Normalize(city), Province = provinceName,
            ProvinceKey = DoeFuelPriceImporter.Normalize(provinceName), Region = "Mimaropa", OilCompany = "PETRON",
            FuelGrade = "DIESEL", MinPricePerLiter = price, MaxPricePerLiter = price,
            WeekStart = new(2026, 10, startDay), WeekEnd = new DateOnly(2026, 10, startDay).AddDays(6),
            SourceUrl = "https://doe.gov.ph/test.pdf", FetchedAtUtc = now
        };
        var currentProvince = Row("", "Marinduque", 27);
        var oldCity = Row("Boac", "Marinduque", 20);
        var otherCity = Row("Gasan", "Marinduque", 27);
        var otherProvince = Row("", "Occidental Mindoro", 27);
        var selected = DoeLocationMatcher.MatchLocation([currentProvince, oldCity, otherCity, otherProvince],
            "Boac", "Marinduque", "Mimaropa");
        Check(selected.Count == 1 && ReferenceEquals(selected[0], currentProvince),
            "Boac must use newer provincial prices, never an old Boac or neighboring city/province.");
        var currentCity = Row("Boac", "Marinduque", 27, 96);
        Check(ReferenceEquals(DoeLocationMatcher.MatchLocation([currentProvince, currentCity], "Boac", "Marinduque")[0],
            currentCity), "City-specific prices win for equal coverage without mixing provincial rows.");
        Check(ReferenceEquals(DoeLocationMatcher.MatchLocation([Row("", "Marinduque", 20), currentCity],
            "City of Boac", "Province of Marinduque")[0], currentCity), "Newer city data and aliases must remain supported.");
        Check(ReferenceEquals(DoeLocationMatcher.MatchLocation([currentProvince, otherCity], null, "Marinduque")[0],
            currentProvince), "A province-only selection must not borrow a municipality's prices.");
        Check(DoeLocationMatcher.MatchLocation([otherCity, otherProvince], "Boac", "Marinduque").Count == 0,
            "No applicable provincial/city data means no result.");
        Check(DoeLocationMatcher.MatchLocation([currentProvince], "Boac", "Marinduque", "Bicol").Count == 0,
            "Conflicting region/province must never fall back.");
        var legacyCity = Row("", "Marinduque", 27);
        legacyCity.CityKey = "BOAC";
        Check(!DoeLocationMatcher.IsProvincePrice(legacyCity) &&
            DoeLocationMatcher.MatchLocation([legacyCity], "Gasan", "Marinduque").Count == 0,
            "Legacy city-key-only data must not be treated as provincial.");
        var laterCopy = Row("", "Marinduque", 27, 98);
        laterCopy.FetchedAtUtc = now.AddMinutes(1);
        Check(ReferenceEquals(DoeLocationMatcher.MatchLocation([currentProvince, laterCopy], "Boac", "Marinduque")[0],
            laterCopy), "Duplicate province company/grade rows resolve to the most recently fetched record.");

        var location = new ResolvedFuelLocation("Boac", "Marinduque", "Mimaropa");
        var feed = DoeFuelPriceEndpoints.ToFeed(location, selected);
        Check(feed.City == "Boac" && feed.Scope == "province" && feed.Prices.Single().City == "" &&
            feed.Prices.Single().Scope == "province", "Feed must preserve requested city while labeling price coverage.");
        Check(DoeFuelPriceEndpoints.ToFeed(location, [currentCity]).Scope == "city",
            "Existing city feed coverage remains city-scoped.");
        var buildResult = typeof(FuelPriceEndpoints).GetMethod("BuildDoeResult", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (JsonElement)buildResult.Invoke(null,
            [new FuelPriceSearchRequest("Boac", "Marinduque", "Mimaropa"), selected, now])!;
        Check(result.GetProperty("status").GetString() == "province_estimate" &&
            result.GetProperty("estimate_area").GetProperty("level").GetString() == "province" &&
            result.GetProperty("estimate_area").GetProperty("name").GetString() == "Marinduque" &&
            result.GetProperty("sources")[0].GetProperty("geographic_coverage").GetString() == "Marinduque",
            "Combined prices must describe actual provincial coverage, not city-specific prices.");
        Console.WriteLine("PASS: provincial validation, identity, newest coverage, city preference, isolation and response provenance");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
