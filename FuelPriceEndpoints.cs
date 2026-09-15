using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

public sealed record FuelPriceSearchRequest(
    string? City,
    string Province,
    string Region);

public sealed record FuelPriceCoordinates(double Latitude, double Longitude);

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
            GeoapifyReverseGeocodingClient geoapify,
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
            if (!TryReadRequest(requestDocument.RootElement, out var coordinates, out var error))
                return ApiResults.BadRequest(error, instanceId);

            if (!geoapify.IsConfigured)
                return ApiResults.ServiceUnavailable(
                    "geocoding_not_configured",
                    instanceId,
                    "The reverse-geocoding service has not been configured.");

            ResolvedFuelLocation? location;
            try
            {
                location = await geoapify.ResolveAsync(
                    coordinates.Latitude,
                    coordinates.Longitude,
                    cancellationToken);
            }
            catch (HttpRequestException)
            {
                return ApiResults.ServiceUnavailable(
                    "geocoding_unavailable",
                    instanceId,
                    "The reverse-geocoding service is temporarily unavailable.");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ApiResults.GatewayTimeout(
                    "geocoding_timeout",
                    instanceId,
                    "The reverse-geocoding service did not respond in time.");
            }
            catch (JsonException)
            {
                return ApiResults.BadGateway(
                    "geocoding_invalid_response",
                    instanceId,
                    "The reverse-geocoding service returned invalid JSON.");
            }

            if (location is null)
                return ApiResults.BadRequest(
                    "Coordinates must resolve to a Philippine province and region.",
                    instanceId);

            var fuelRequest = new FuelPriceSearchRequest(
                location.City,
                location.Province,
                location.Region);

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var cityKey = NormalizeLocation(fuelRequest.City);
            var provinceKey = NormalizeLocation(fuelRequest.Province)!;
            var regionKey = NormalizeLocation(fuelRequest.Region)!;

            var cached = await db.FuelPriceCaches
                .AsNoTracking()
                .Where(c =>
                    ((c.ProvinceKey == provinceKey &&
                      ((cityKey != null &&
                        c.Scope == FuelPriceCacheScopes.City &&
                        c.CityKey == cityKey) ||
                       c.Scope == FuelPriceCacheScopes.Province)) ||
                     (c.Scope == FuelPriceCacheScopes.Region &&
                      c.RegionKey == regionKey)) &&
                    c.RefreshAfter > now)
                .OrderBy(c => c.Scope == FuelPriceCacheScopes.City ? 0 :
                    c.Scope == FuelPriceCacheScopes.Province ? 1 : 2)
                .ThenByDescending(c => c.CachedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (cached is not null)
            {
                using var cachedDocument = JsonDocument.Parse(cached.ResultJson);
                if (TryGetFreshDataAsOf(
                        cachedDocument.RootElement,
                        now,
                        out var cachedDataAsOfUtc))
                {
                    await db.FuelPriceCaches
                        .Where(c => c.Id == cached.Id)
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(
                                c => c.HitCount,
                                c => c.HitCount + 1),
                            cancellationToken);

                    return ApiResults.Ok(
                        new FuelPriceApiResponse(
                            WithRequestLocation(cachedDocument.RootElement, coordinates, location),
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
                var response = await openAi.CreateFuelPriceResponseAsync(
                    fuelRequest,
                    now,
                    cancellationToken);
                var cachedAt = timeProvider.GetUtcNow().UtcDateTime;
                var hasUsablePrices = HasUsablePrices(response.Result);
                var dataAsOfUtc = default(DateTime);
                if (hasUsablePrices &&
                    !TryGetFreshDataAsOf(response.Result, cachedAt, out dataAsOfUtc))
                {
                    return ApiResults.BadGateway(
                        "ai_invalid_response",
                        instanceId,
                        "The fuel-price agent did not provide evidence verified within the last seven days.");
                }

                var responseCacheScope = TryGetResponseCacheScope(response.Result);
                var responseEstimateNameKey = NormalizeLocation(
                    TryGetEstimateAreaName(response.Result));
                var responseCity = TryGetResponseLocation(response.Result, "city");
                var responseProvince = TryGetResponseLocation(response.Result, "province");
                var responseRegion = TryGetResponseLocation(response.Result, "region");
                var responseCityKey = NormalizeLocation(responseCity);
                var responseProvinceKey = NormalizeLocation(responseProvince);
                var responseRegionKey = NormalizeLocation(responseRegion);
                var cacheStored =
                    hasUsablePrices &&
                    responseCacheScope is not null &&
                    responseRegionKey == regionKey &&
                    responseEstimateNameKey == (responseCacheScope switch
                    {
                        FuelPriceCacheScopes.City => cityKey,
                        FuelPriceCacheScopes.Province => provinceKey,
                        FuelPriceCacheScopes.Region => regionKey,
                        _ => null
                    }) &&
                    (responseCacheScope == FuelPriceCacheScopes.Region ||
                     responseProvinceKey == provinceKey) &&
                    (responseCacheScope != FuelPriceCacheScopes.City ||
                     (cityKey is not null && responseCityKey == cityKey));
                DateTime? refreshAfter = null;

                if (cacheStored)
                {
                    var cacheRefreshAfter = GetRefreshAfter(cachedAt, dataAsOfUtc);
                    refreshAfter = cacheRefreshAfter;
                    var isCityCache = responseCacheScope == FuelPriceCacheScopes.City;
                    db.FuelPriceCaches.Add(new FuelPriceCache
                    {
                        Scope = responseCacheScope!,
                        City = isCityCache ? responseCity : null,
                        Province = responseProvince ?? fuelRequest.Province,
                        Region = responseRegion,
                        CityKey = isCityCache ? responseCityKey : null,
                        ProvinceKey = provinceKey,
                        RegionKey = regionKey,
                        ResultJson = response.Result.GetRawText(),
                        Model = response.Model,
                        CachedAt = cachedAt,
                        RefreshAfter = cacheRefreshAfter
                    });
                    await db.SaveChangesAsync(cancellationToken);
                }

                return ApiResults.Ok(
                    new FuelPriceApiResponse(
                        WithRequestLocation(response.Result, coordinates, location),
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
                        refreshAfter),
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
        out FuelPriceCoordinates request,
        out string error)
    {
        request = new FuelPriceCoordinates(0, 0);
        error = "";

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "Fuel-price request must be a JSON object.";
            return false;
        }

        double? latitude = null;
        double? longitude = null;
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


        request = new FuelPriceCoordinates(latitude.Value, longitude.Value);
        return true;
    }

    private static bool TryReadFiniteNumber(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out number) &&
               double.IsFinite(number);
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
            FuelPriceCacheScopes.Region => FuelPriceCacheScopes.Region,
            _ => null
        };
    }

    private static string? TryGetEstimateAreaName(JsonElement result)
    {
        if (!result.TryGetProperty("estimate_area", out var estimateArea) ||
            estimateArea.ValueKind != JsonValueKind.Object ||
            !estimateArea.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String)
            return null;

        return name.GetString()?.Trim();
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

    private static JsonElement WithRequestLocation(
        JsonElement priceResult,
        FuelPriceCoordinates coordinates,
        ResolvedFuelLocation location)
    {
        var result = JsonNode.Parse(priceResult.GetRawText())!.AsObject();
        result["location"] = new JsonObject
        {
            ["latitude"] = coordinates.Latitude,
            ["longitude"] = coordinates.Longitude,
            ["resolved_area"] = location.City is null
                ? $"{location.Province}, {location.Region}"
                : $"{location.City}, {location.Province}, {location.Region}",
            ["city"] = location.City,
            ["province"] = location.Province,
            ["region"] = location.Region,
            ["country"] = "Philippines",
            ["geocoding_attribution"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "OpenStreetMap contributors",
                    ["url"] = "https://www.openstreetmap.org/copyright"
                },
                new JsonObject
                {
                    ["name"] = "Geoapify",
                    ["url"] = "https://www.geoapify.com/"
                }
            }
        };
        return JsonSerializer.SerializeToElement(result);
    }

    private static bool TryGetFreshDataAsOf(
        JsonElement result,
        DateTime nowUtc,
        out DateTime dataAsOfUtc)
    {
        dataAsOfUtc = default;
        if (!result.TryGetProperty("data_as_of", out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = value.GetString();
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        if (DateOnly.TryParseExact(
                candidate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            var philippines = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");
            var localMidnight = DateTime.SpecifyKind(
                date.ToDateTime(TimeOnly.MinValue),
                DateTimeKind.Unspecified);
            dataAsOfUtc = TimeZoneInfo.ConvertTimeToUtc(localMidnight, philippines);
        }
        else if (DateTimeOffset.TryParse(
                     candidate,
                     CultureInfo.InvariantCulture,
                     DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                     out var timestamp))
        {
            dataAsOfUtc = timestamp.UtcDateTime;
        }
        else
        {
            return false;
        }

        var utcNow = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        return dataAsOfUtc <= utcNow && dataAsOfUtc >= utcNow.AddDays(-7);
    }

    private static DateTime GetRefreshAfter(DateTime cachedAtUtc, DateTime dataAsOfUtc)
    {
        var utc = DateTime.SpecifyKind(cachedAtUtc, DateTimeKind.Utc);
        var philippines = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, philippines);
        var daysUntilTuesday = ((int)DayOfWeek.Tuesday - (int)local.DayOfWeek + 7) % 7;
        var nextTuesdayAtSix = local.Date.AddDays(daysUntilTuesday).AddHours(6);

        if (nextTuesdayAtSix <= local)
            nextTuesdayAtSix = nextTuesdayAtSix.AddDays(7);

        var weeklyRefreshUtc = TimeZoneInfo.ConvertTimeToUtc(nextTuesdayAtSix, philippines);
        var evidenceExpiryUtc = DateTime.SpecifyKind(dataAsOfUtc, DateTimeKind.Utc).AddDays(7);
        return weeklyRefreshUtc < evidenceExpiryUtc ? weeklyRefreshUtc : evidenceExpiryUtc;
    }
}
