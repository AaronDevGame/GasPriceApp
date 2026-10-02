using System.Globalization;
using System.Text.Json;

public static class DoeFuelPriceEndpoints
{
    public static void MapDoeFuelPriceEndpoints(this WebApplication app,
        AuthService authService, string instanceId)
    {
        app.MapGet(ApiRoutes.DoeFuelPrices, async (
            HttpRequest request, AppDbContext db, GeoapifyReverseGeocodingClient geoapify,
            DoeFuelPriceImporter importer, TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var auth = await PlayerAuthentication.AuthenticateAsync(request, db, authService);
            if (!auth.IsValid || auth.Guest is null)
                return auth.IsBadRequest
                    ? ApiResults.BadRequest(auth.Error, instanceId)
                    : ApiResults.Unauthorized(auth.Error, instanceId);
            if (request.Query.Count != 2 ||
                !request.Query.TryGetValue("latitude", out var latValues) || latValues.Count != 1 ||
                !request.Query.TryGetValue("longitude", out var lonValues) || lonValues.Count != 1 ||
                !double.TryParse(latValues[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
                !double.TryParse(lonValues[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude) ||
                !double.IsFinite(latitude) || !double.IsFinite(longitude) ||
                latitude is < -90 or > 90 || longitude is < -180 or > 180)
                return ApiResults.BadRequest("Latitude and longitude must be valid coordinates.", instanceId);
            if (!geoapify.IsConfigured)
                return ApiResults.ServiceUnavailable("geocoding_not_configured", instanceId);
            ResolvedFuelLocation? location;
            try { location = await geoapify.ResolveAsync(latitude, longitude, cancellationToken); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return ApiResults.GatewayTimeout("geocoding_timeout", instanceId); }
            catch (HttpRequestException)
            { return ApiResults.ServiceUnavailable("geocoding_unavailable", instanceId); }
            catch (JsonException)
            { return ApiResults.BadGateway("geocoding_invalid_response", instanceId); }
            if (location is null)
                return ApiResults.BadRequest("No Philippine area was found for these coordinates.", instanceId);

            var rows = await importer.GetAsync(db, location,
                timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            return ApiResults.Ok(ToFeed(location, rows), "doe_fuel_prices", instanceId);
        });

        app.MapPost(ApiRoutes.AdminDoeFuelPricesImport, async (
            HttpRequest request, AppDbContext db, DoeFuelPriceImporter importer,
            TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            if (!request.HasJsonContentType())
                return ApiResults.BadRequest("Request body must be JSON.", instanceId);
            JsonDocument document;
            try { document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken); }
            catch (JsonException)
            { return ApiResults.BadRequest("Request body must be valid JSON.", instanceId); }
            using var _ = document;
            var root = document.RootElement;
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Any(p =>
                    !names.Add(p.Name) || p.Name is not ("city" or "province" or "region" or "sourceUrl")) ||
                !root.TryGetProperty("city", out var cityValue) || cityValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("province", out var provinceValue) || provinceValue.ValueKind != JsonValueKind.String)
                return ApiResults.BadRequest("Provide city and province strings; optional region and sourceUrl.", instanceId);
            var city = cityValue.GetString()?.Trim();
            var province = provinceValue.GetString()?.Trim();
            var region = root.TryGetProperty("region", out var regionValue) && regionValue.ValueKind == JsonValueKind.String
                ? regionValue.GetString()?.Trim() : null;
            var url = root.TryGetProperty("sourceUrl", out var urlValue) && urlValue.ValueKind == JsonValueKind.String
                ? urlValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(city) || city.Length > 100 ||
                string.IsNullOrWhiteSpace(province) || province.Length > 100 ||
                region is { Length: > 100 } ||
                (root.TryGetProperty("region", out regionValue) && regionValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) ||
                (root.TryGetProperty("sourceUrl", out urlValue) && urlValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                return ApiResults.BadRequest("Invalid city, province, region, or sourceUrl.", instanceId);
            if (url is not null)
            {
                try { url = FuelAdjustmentImporter.ValidatePdfUrl(url); }
                catch (ArgumentException)
                { return ApiResults.BadRequest("sourceUrl must be a DOE PDF URL.", instanceId); }
            }
            if (!importer.IsConfigured)
                return ApiResults.ServiceUnavailable("doe_price_extractor_not_configured", instanceId);
            try
            {
                var result = await importer.ImportAsync(db,
                    new ResolvedFuelLocation(city, province, region),
                    timeProvider.GetUtcNow().UtcDateTime, url, cancellationToken);
                return ApiResults.Ok(result, "doe_fuel_prices_" + result.Status, instanceId);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return ApiResults.GatewayTimeout("doe_price_import_timeout", instanceId); }
            catch (HttpRequestException)
            { return ApiResults.BadGateway("doe_price_upstream_unavailable", instanceId); }
            catch (InvalidDataException ex)
            { return ApiResults.BadGateway("doe_price_import_invalid", instanceId, ex.Message); }
            catch (JsonException)
            { return ApiResults.BadGateway("doe_price_import_invalid", instanceId); }
        });
    }

    public static DoeFuelPriceFeed ToFeed(ResolvedFuelLocation location,
        IReadOnlyList<DoeFuelPrice> rows) => new(
        location.City, location.Province, location.Region,
        rows.FirstOrDefault()?.WeekStart, rows.FirstOrDefault()?.WeekEnd,
        rows.Select(p => new DoeFuelPriceItem(p.City, p.Province, p.Region,
            p.OilCompany, p.FuelGrade, p.MinPricePerLiter, p.MaxPricePerLiter,
            p.WeekStart, p.WeekEnd, p.SourceUrl, p.FetchedAtUtc)).ToArray());
}
