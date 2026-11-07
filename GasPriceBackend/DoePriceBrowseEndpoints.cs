using Microsoft.EntityFrameworkCore;

public sealed record DoePriceLocation(string City, string Province)
{
    public string Scope => City.Length == 0 ? "province" : "city";
}
public sealed record DoePriceBrowseFeed(
    IReadOnlyList<DoePriceLocation> Locations, DoeFuelPriceFeed? Selected);

public static class DoePriceBrowseEndpoints
{
    public static void MapDoePriceBrowseEndpoints(this WebApplication app,
        AuthService authService, string instanceId)
    {
        app.MapGet(ApiRoutes.DoeFuelPricesBrowse, async (
            HttpRequest request, AppDbContext db, DoeFuelPriceImporter importer,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var auth = await PlayerAuthentication.AuthenticateAsync(request, db, authService);
            if (!auth.IsValid || auth.Guest is null)
                return auth.IsBadRequest
                    ? ApiResults.BadRequest(auth.Error, instanceId)
                    : ApiResults.Unauthorized(auth.Error, instanceId);

            if (request.Query.Any(p => p.Key is not ("city" or "province") || p.Value.Count != 1) ||
                (request.Query.ContainsKey("city") && !request.Query.ContainsKey("province")))
                return ApiResults.BadRequest("Provide province with an optional city, once each.", instanceId);
            var city = request.Query["city"].ToString().Trim();
            var province = request.Query["province"].ToString().Trim();
            if (request.Query.ContainsKey("province") &&
                (city.Length > 100 || province.Length is < 1 or > 100))
                return ApiResults.BadRequest("Province must be 1 to 100 characters; optional city at most 100.", instanceId);

            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
                timeProvider.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila")).DateTime);
            var earliest = today.AddDays(-7);
            var current = db.DoeFuelPrices.AsNoTracking().Where(p =>
                p.WeekEnd >= earliest && p.WeekStart <= today);
            var storedLocations = await current
                .Where(p => p.Province != "")
                .Select(p => new { City = p.City == "" ? p.CityKey : p.City, p.Province }).Distinct()
                .OrderBy(p => p.Province).ThenBy(p => p.City)
                .ToArrayAsync(cancellationToken);
            var locations = storedLocations
                .Select(p => new DoePriceLocation(p.City, p.Province)).ToArray();

            var selectedLocation = request.Query.ContainsKey("province")
                ? new DoePriceLocation(city, province)
                : locations.FirstOrDefault();

            DoeFuelPriceFeed? selected = null;
            if (selectedLocation is not null)
            {
                var location = new ResolvedFuelLocation(selectedLocation.City,
                    selectedLocation.Province, null);
                var rows = await importer.ReadAsync(db, location,
                    timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
                if (rows.Count == 0 && request.Query.ContainsKey("province"))
                    return ApiResults.NotFound("No recent DOE prices were found for that location.",
                        instanceId, ApiRoutes.DoeFuelPricesBrowse);
                selected = DoeFuelPriceEndpoints.ToFeed(
                    location, rows);
            }
            return ApiResults.Ok(new DoePriceBrowseFeed(locations, selected),
                "doe_price_browse", instanceId);
        });
    }
}
