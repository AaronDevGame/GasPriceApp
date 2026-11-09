using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

public static class FuelMapEndpoints
{
    public const int MaxRequestBytes = 32768;

    public static void MapFuelMapEndpoints(this WebApplication app, AuthService authService, string instanceId)
    {
        app.MapGet(ApiRoutes.FuelMapIndex, (HttpRequest request, FuelMapGeography geography) =>
            request.Query.Count != 0
                ? Invalid(request, "Geographic index does not accept query parameters.", instanceId)
                : Asset(request, geography.IndexAsset, immutable: false));

        app.MapGet(ApiRoutes.FuelMapBoundary, (HttpRequest request, string version, string fileId, FuelMapGeography geography) =>
        {
            request.HttpContext.Response.Headers.CacheControl = "no-store";
            if (request.Query.Count != 0)
                return ApiResults.BadRequest("Boundary files do not accept query parameters.", instanceId);
            var asset = version == FuelMapGeography.Version ? geography.Boundary(fileId) : null;
            return asset is null
                ? ApiResults.NotFound("Unknown boundary version or file ID.", instanceId, request.Path)
                : Asset(request, asset, immutable: true);
        });

        app.MapPost(ApiRoutes.FuelMapPrices, async (HttpRequest request, AppDbContext db,
            FuelMapGeography geography, FuelMapPriceService prices, CancellationToken cancellationToken) =>
        {
            var auth = await PlayerAuthentication.AuthenticateAsync(request, db, authService);
            if (!auth.IsValid || auth.Guest is null)
                return auth.IsBadRequest ? ApiResults.BadRequest(auth.Error, instanceId)
                    : ApiResults.Unauthorized(auth.Error, instanceId);
            if (request.Query.Count != 0 || !request.HasJsonContentType() || request.ContentLength is > MaxRequestBytes)
                return ApiResults.BadRequest("Provide a JSON body of at most 32768 bytes without query parameters.", instanceId);
            // Buffer at most limit + 1 even for chunked transfer; reject before unbounded JSON parsing.
            var limitFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limitFeature is { IsReadOnly: false }) limitFeature.MaxRequestBodySize = MaxRequestBytes;
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            try
            {
                while (buffer.Length <= MaxRequestBytes)
                {
                    var count = await request.Body.ReadAsync(chunk.AsMemory(0,
                        (int)Math.Min(chunk.Length, MaxRequestBytes + 1 - buffer.Length)), cancellationToken);
                    if (count == 0) break;
                    buffer.Write(chunk, 0, count);
                }
            }
            catch (BadHttpRequestException)
            { return ApiResults.BadRequest("Request body exceeds the allowed size.", instanceId); }
            if (buffer.Length > MaxRequestBytes)
                return ApiResults.BadRequest("Request body exceeds the allowed size.", instanceId);
            JsonDocument document;
            try { document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 3 }); }
            catch (JsonException) { return ApiResults.BadRequest("Request body must be valid JSON.", instanceId); }
            using (document)
            {
                var root = document.RootElement;
                var names = new HashSet<string>(StringComparer.Ordinal);
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(property =>
                        !names.Add(property.Name) || property.Name is not ("boundaryVersion" or "cityIds")) ||
                    !root.TryGetProperty("boundaryVersion", out var version) || version.ValueKind != JsonValueKind.String ||
                    version.GetString() != FuelMapGeography.Version ||
                    !root.TryGetProperty("cityIds", out var ids) || ids.ValueKind != JsonValueKind.Array ||
                    ids.GetArrayLength() is < 1 or > FuelMapPriceService.MaxCities)
                    return ApiResults.BadRequest("Provide the current boundaryVersion and 1 to 2000 cityIds.", instanceId);
                var cities = new HashSet<string>(StringComparer.Ordinal);
                var ordered = new List<string>();
                foreach (var value in ids.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: 10 } id ||
                        id.Any(character => character is < '0' or > '9') || !geography.Cities.ContainsKey(id))
                        return ApiResults.BadRequest("Each cityId must be a known Philippine 10-digit PSGC string.", instanceId);
                    if (cities.Add(id)) ordered.Add(id);
                }
                var feed = await prices.ReadAsync(ordered, cancellationToken);
                return ApiResults.Ok(feed, "fuel_map_prices", instanceId);
            }
        });
    }

    private static IResult Invalid(HttpRequest request, string error, string instanceId)
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        return ApiResults.BadRequest(error, instanceId);
    }

    private static IResult Asset(HttpRequest request, FuelMapAsset asset, bool immutable)
    {
        var response = request.HttpContext.Response;
        // q=0 must not select gzip; malformed Accept-Encoding simply falls back to identity.
        var gzip = request.Headers.AcceptEncoding.ToString().Split(',').Any(part =>
        {
            var parameters = part.Trim().Split(';');
            return parameters[0].Trim().Equals("gzip", StringComparison.OrdinalIgnoreCase) &&
                (parameters.Length == 1 || (parameters.Length == 2 &&
                    parameters[1].Trim().StartsWith("q=", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(parameters[1].Trim()[2..], System.Globalization.NumberStyles.AllowDecimalPoint,
                        System.Globalization.CultureInfo.InvariantCulture, out var quality) && quality is > 0 and <= 1));
        });
        var tag = gzip ? asset.GzipETag : asset.ETag;
        response.Headers.CacheControl = immutable ? "public, max-age=31536000, immutable" : "public, max-age=300";
        response.Headers.ETag = tag;
        response.Headers.Vary = "Accept-Encoding";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        if (EntityTagHeaderValue.TryParseList(request.Headers.IfNoneMatch.Select(value => value ?? "").ToArray(), out var tags) &&
            tags.Any(value => value.Tag == "*" || value.Tag == tag))
            return Results.StatusCode(StatusCodes.Status304NotModified);
        if (gzip) response.Headers.ContentEncoding = "gzip";
        return Results.Bytes(gzip ? asset.Gzip : asset.Bytes, "application/json; charset=utf-8");
    }
}
