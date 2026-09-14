using System.Text.Json;
using Microsoft.EntityFrameworkCore;

public sealed record FuelPriceSearchRequest(
    double Latitude,
    double Longitude,
    string? City,
    string Province);

public sealed record FuelPriceApiResponse(
    JsonElement Result,
    string Model,
    AiChatTokenUsage? Usage,
    OpenAiCostEstimate? EstimatedCost,
    bool UsedWebSearch,
    bool FromCache,
    bool CacheStored,
    string? CacheScope,
    DateTime? CachedAt,
    DateTime? RefreshAfter);

public static class FuelPriceEndpoints
{
    public static void MapFuelPriceEndpoints(
        this WebApplication app,
        AuthService authService,
        string instanceId)
    {
        app.MapPost(ApiRoutes.AiFuelPrices, async (
            HttpRequest request,
            AppDbContext db,
            OpenAiResponsesClient openAi,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var auth = await PlayerAuthentication.AuthenticateAsync(request, db, authService);
            if (!auth.IsValid || auth.Guest is null)
                return auth.IsBadRequest
                    ? ApiResults.BadRequest(auth.Error, instanceId)
                    : ApiResults.Unauthorized(auth.Error, instanceId);

            if (!request.HasJsonContentType())
                return ApiResults.BadRequest("Request body must be JSON.", instanceId);

            JsonDocument requestDocument;
            try
            {
                requestDocument = await JsonDocument.ParseAsync(
                    request.Body,
                    cancellationToken: cancellationToken);
            }
            catch (JsonException)
            {
                return ApiResults.BadRequest("Request body must be valid JSON.", instanceId);
            }

            using var _ = requestDocument;
            if (!TryReadRequest(requestDocument.RootElement, out var fuelRequest, out var error))
                return ApiResults.BadRequest(error, instanceId);

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var cityKey = NormalizeLocation(fuelRequest.City);
            var provinceKey = NormalizeLocation(fuelRequest.Province)!;

            var cached = await db.FuelPriceCaches
                .AsNoTracking()
                .Where(c =>
                    c.ProvinceKey == provinceKey &&
                    ((cityKey != null &&
                      c.Scope == FuelPriceCacheScopes.City &&
                      c.CityKey == cityKey) ||
                     c.Scope == FuelPriceCacheScopes.Province ||
                     (cityKey == null && c.Scope == FuelPriceCacheScopes.City)) &&
                    c.RefreshAfter > now)
                .OrderBy(c => cityKey != null
                    ? (c.Scope == FuelPriceCacheScopes.City ? 0 : 1)
                    : (c.Scope == FuelPriceCacheScopes.Province ? 0 : 1))
                .ThenByDescending(c => c.CachedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (cached is not null)
            {
                using var cachedDocument = JsonDocument.Parse(cached.ResultJson);
                return ApiResults.Ok(
                    new FuelPriceApiResponse(
                        cachedDocument.RootElement.Clone(),
                        cached.Model,
                        null,
                        null,
                        false,
                        true,
                        true,
                        cached.Scope,
                        cached.CachedAt,
                        cached.RefreshAfter),
                    "fuel_price_response",
                    instanceId);
            }

            if (!openAi.IsConfigured)
                return ApiResults.ServiceUnavailable(
                    "ai_service_not_configured",
                    instanceId,
                    "The server AI integration has not been configured.");

            if (!openAi.IsFuelPriceAgentConfigured)
                return ApiResults.ServiceUnavailable(
                    "fuel_price_agent_not_configured",
                    instanceId,
                    "The fuel-price agent instructions are unavailable.");

            try
            {
                var response = await openAi.CreateFuelPriceResponseAsync(fuelRequest, cancellationToken);
                var cachedAt = timeProvider.GetUtcNow().UtcDateTime;
                var refreshAfter = GetRefreshAfter(cachedAt);
                var responseCacheScope = TryGetResponseCacheScope(response.Result);
                var responseCity = TryGetResponseLocation(response.Result, "city");
                var responseProvince = TryGetResponseLocation(response.Result, "province");
                var responseCityKey = NormalizeLocation(responseCity);
                var responseProvinceKey = NormalizeLocation(responseProvince);
                var cacheStored =
                    HasUsablePrices(response.Result) &&
                    responseCacheScope is not null &&
                    responseProvinceKey == provinceKey &&
                    (responseCacheScope != FuelPriceCacheScopes.City ||
                     (responseCityKey is not null &&
                      (cityKey is null || responseCityKey == cityKey)));

                if (cacheStored)
                {
                    var isCityCache = responseCacheScope == FuelPriceCacheScopes.City;
                    db.FuelPriceCaches.Add(new FuelPriceCache
                    {
                        Scope = responseCacheScope!,
                        City = isCityCache ? responseCity : null,
                        Province = responseProvince!,
                        CityKey = isCityCache ? responseCityKey : null,
                        ProvinceKey = provinceKey,
                        ResultJson = response.Result.GetRawText(),
                        Model = response.Model,
                        CachedAt = cachedAt,
                        RefreshAfter = refreshAfter
                    });
                    await db.SaveChangesAsync(cancellationToken);
                }

                return ApiResults.Ok(
                    new FuelPriceApiResponse(
                        response.Result,
                        response.Model,
                        response.Usage,
                        OpenAiPricing.Estimate(
                            response.Model,
                            response.Usage,
                            response.WebSearchCalls),
                        response.UsedWebSearch,
                        false,
                        cacheStored,
                        cacheStored ? responseCacheScope : null,
                        cacheStored ? cachedAt : null,
                        cacheStored ? refreshAfter : null),
                    "fuel_price_response",
                    instanceId);
            }
            catch (OpenAiUpstreamException ex) when (ex.StatusCode is 401 or 403)
            {
                return ApiResults.ServiceUnavailable(
                    "ai_service_not_configured",
                    instanceId,
                    "The server AI integration could not authenticate with its provider.");
            }
            catch (OpenAiUpstreamException ex) when (ex.StatusCode == 429)
            {
                return ApiResults.ServiceUnavailable(
                    "ai_service_rate_limited",
                    instanceId,
                    "The AI service is temporarily rate limited. Try again later.");
            }
            catch (OpenAiUpstreamException)
            {
                return ApiResults.BadGateway(
                    "ai_service_error",
                    instanceId,
                    "The AI service could not complete the fuel-price research.");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ApiResults.GatewayTimeout(
                    "ai_service_timeout",
                    instanceId,
                    "The AI service did not respond in time.");
            }
            catch (HttpRequestException)
            {
                return ApiResults.ServiceUnavailable(
                    "ai_service_unavailable",
                    instanceId,
                    "The AI service is temporarily unavailable.");
            }
            catch (JsonException)
            {
                return ApiResults.BadGateway(
                    "ai_invalid_response",
                    instanceId,
                    "The fuel-price agent returned invalid JSON.");
            }
        });
    }

    private static bool TryReadRequest(
        JsonElement root,
        out FuelPriceSearchRequest request,
        out string error)
    {
        request = new FuelPriceSearchRequest(0, 0, null, "");
        error = "";

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "Fuel-price request must be a JSON object.";
            return false;
        }

        double? latitude = null;
        double? longitude = null;
        string? city = null;
        string? province = null;
        var fields = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in root.EnumerateObject())
        {
            if (!fields.Add(property.Name))
            {
                error = $"Field '{property.Name}' must be provided only once.";
                return false;
            }

            switch (property.Name)
            {
                case "latitude":
                    if (!TryReadFiniteNumber(property.Value, out var latitudeValue))
                    {
                        error = "Latitude must be a number.";
                        return false;
                    }
                    latitude = latitudeValue;
                    break;
                case "longitude":
                    if (!TryReadFiniteNumber(property.Value, out var longitudeValue))
                    {
                        error = "Longitude must be a number.";
                        return false;
                    }
                    longitude = longitudeValue;
                    break;
                case "city":
                    if (property.Value.ValueKind == JsonValueKind.Null)
                    {
                        city = null;
                        break;
                    }
                    if (!TryReadLocation(property.Value, out city))
                    {
                        error = "City must be null or a non-empty string of at most 100 characters.";
                        return false;
                    }
                    break;
                case "province":
                    if (!TryReadLocation(property.Value, out province))
                    {
                        error = "Province must be a non-empty string of at most 100 characters.";
                        return false;
                    }
                    break;
                default:
                    error = $"Unknown fuel-price field '{property.Name}'.";
                    return false;
            }
        }

        if (latitude is null)
        {
            error = "Latitude is required.";
            return false;
        }

        if (latitude is < -90 or > 90)
        {
            error = "Latitude must be between -90 and 90.";
            return false;
        }

        if (longitude is null)
        {
            error = "Longitude is required.";
            return false;
        }

        if (longitude is < -180 or > 180)
        {
            error = "Longitude must be between -180 and 180.";
            return false;
        }


        if (province is null)
        {
            error = "Province is required.";
            return false;
        }

        request = new FuelPriceSearchRequest(latitude.Value, longitude.Value, city, province);
        return true;
    }

    private static bool TryReadFiniteNumber(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out number) &&
               double.IsFinite(number);
    }

    private static bool TryReadLocation(JsonElement value, out string? location)
    {
        location = null;
        if (value.ValueKind != JsonValueKind.String)
            return false;

        var candidate = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 100)
            return false;

        location = candidate;
        return true;
    }

    private static string? NormalizeLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return null;

        return string.Join(' ', location.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToUpperInvariant();
    }

    private static bool HasUsablePrices(JsonElement result)
    {
        if (!result.TryGetProperty("prices", out var prices) ||
            prices.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var price in prices.EnumerateObject())
        {
            if (price.Value.ValueKind == JsonValueKind.Object &&
                price.Value.TryGetProperty("min_price", out var minimum) &&
                minimum.ValueKind == JsonValueKind.Number &&
                price.Value.TryGetProperty("max_price", out var maximum) &&
                maximum.ValueKind == JsonValueKind.Number)
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryGetResponseCacheScope(JsonElement result)
    {
        if (!result.TryGetProperty("estimate_area", out var estimateArea) ||
            estimateArea.ValueKind != JsonValueKind.Object ||
            !estimateArea.TryGetProperty("level", out var level) ||
            level.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return level.GetString() switch
        {
            FuelPriceCacheScopes.City => FuelPriceCacheScopes.City,
            FuelPriceCacheScopes.Province => FuelPriceCacheScopes.Province,
            _ => null
        };
    }

    private static string? TryGetResponseLocation(JsonElement result, string field)
    {
        if (!result.TryGetProperty("location", out var location) ||
            location.ValueKind != JsonValueKind.Object ||
            !location.TryGetProperty(field, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString()?.Trim();
    }

    private static DateTime GetRefreshAfter(DateTime cachedAtUtc)
    {
        var utc = DateTime.SpecifyKind(cachedAtUtc, DateTimeKind.Utc);
        var philippines = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, philippines);
        var daysUntilTuesday = ((int)DayOfWeek.Tuesday - (int)local.DayOfWeek + 7) % 7;
        var nextTuesdayAtSix = local.Date.AddDays(daysUntilTuesday).AddHours(6);

        if (nextTuesdayAtSix <= local)
            nextTuesdayAtSix = nextTuesdayAtSix.AddDays(7);

        var weeklyRefreshUtc = TimeZoneInfo.ConvertTimeToUtc(nextTuesdayAtSix, philippines);
        var sevenDaysUtc = utc.AddDays(7);
        return weeklyRefreshUtc < sevenDaysUtc ? weeklyRefreshUtc : sevenDaysUtc;
    }
}
