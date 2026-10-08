using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

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
            DoeImportWorker worker, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            if (!request.HasJsonContentType() || request.ContentLength is > 4096)
                return ApiResults.BadRequest("Provide a small JSON request body.", instanceId);
            var bodySizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySizeFeature is { IsReadOnly: false }) bodySizeFeature.MaxRequestBodySize = 4096;
            JsonDocument document;
            try { document = await JsonDocument.ParseAsync(request.Body,
                new JsonDocumentOptions { MaxDepth = 3 }, cancellationToken); }
            catch (JsonException)
            { return ApiResults.BadRequest("Request body must be valid JSON.", instanceId); }
            using var _ = document;
            var root = document.RootElement;
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Any(p =>
                    !names.Add(p.Name) || p.Name is not ("city" or "province" or "region" or "sourceUrl")))
                return ApiResults.BadRequest("Provide sourceUrl alone, or city and province with optional region and sourceUrl.", instanceId);
            var hasCity = root.TryGetProperty("city", out var cityValue);
            var hasProvince = root.TryGetProperty("province", out var provinceValue);
            var hasRegion = root.TryGetProperty("region", out var regionValue);
            var hasUrl = root.TryGetProperty("sourceUrl", out var urlValue);
            var reportOnly = !hasCity && !hasProvince && !hasRegion;
            if ((!reportOnly && (!hasCity || cityValue.ValueKind != JsonValueKind.String ||
                    !hasProvince || provinceValue.ValueKind != JsonValueKind.String)) ||
                (hasRegion && regionValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) ||
                (hasUrl && urlValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                return ApiResults.BadRequest("Provide sourceUrl alone, or city and province with optional region and sourceUrl.", instanceId);
            var city = hasCity ? cityValue.GetString()?.Trim() : null;
            var province = hasProvince ? provinceValue.GetString()?.Trim() : null;
            var region = hasRegion && regionValue.ValueKind == JsonValueKind.String
                ? regionValue.GetString()?.Trim() : null;
            var url = hasUrl && urlValue.ValueKind == JsonValueKind.String
                ? urlValue.GetString() : null;
            if ((!reportOnly && (string.IsNullOrWhiteSpace(city) || city.Length > 100 ||
                    string.IsNullOrWhiteSpace(province) || province.Length > 100)) ||
                region is { Length: > 100 } || (reportOnly && string.IsNullOrWhiteSpace(url)))
                return ApiResults.BadRequest("Invalid city, province, region, or sourceUrl.", instanceId);
            if (url is not null)
            {
                try { url = FuelAdjustmentImporter.ValidatePdfUrl(url); }
                catch (ArgumentException)
                { return ApiResults.BadRequest("sourceUrl must be a DOE PDF URL.", instanceId); }
            }
            if (!importer.IsConfigured)
                return ApiResults.ServiceUnavailable("doe_price_extractor_not_configured", instanceId);
            var job = new DoeImportJob
            {
                Id = Guid.NewGuid(), Mode = reportOnly ? "report" : "location",
                CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
                RequestJson = JsonSerializer.Serialize(new DoeSingleImportRequest(city, province, region, url))
            };
            db.DoeImportJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
            worker.Wake();
            return DoeImportEndpoints.Accepted(request, job, instanceId);
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
