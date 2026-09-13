using System.Text.Json;

public sealed record FuelPriceSearchRequest(
    double Latitude,
    double Longitude,
    double RadiusKm,
    string? Barangay,
    string? Municipality,
    string? City,
    string? Province);

public sealed record FuelPriceApiResponse(
    JsonElement Result,
    string Model,
    AiChatTokenUsage? Usage,
    bool UsedWebSearch);

public static class FuelPriceEndpoints
{
    private const double MinRadiusKm = 0.1;
    private const double MaxRadiusKm = 100;
    private const int MaxAreaNameLength = 120;

    public static void MapFuelPriceEndpoints(
        this WebApplication app,
        AuthService authService,
        string instanceId)
    {
        app.MapPost(ApiRoutes.AiFuelPrices, async (
            HttpRequest request,
            AppDbContext db,
            OpenAiResponsesClient openAi,
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
                return ApiResults.Ok(
                    new FuelPriceApiResponse(
                        response.Result,
                        response.Model,
                        response.Usage,
                        response.UsedWebSearch),
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
        request = new FuelPriceSearchRequest(0, 0, 0, null, null, null, null);
        error = "";

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "Fuel-price request must be a JSON object.";
            return false;
        }

        double? latitude = null;
        double? longitude = null;
        double? radiusKm = null;
        string? barangay = null;
        string? municipality = null;
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
                case "radiusKm":
                    if (!TryReadFiniteNumber(property.Value, out var radiusValue))
                    {
                        error = "RadiusKm must be a number.";
                        return false;
                    }
                    radiusKm = radiusValue;
                    break;
                case "barangay":
                    if (!TryReadAreaName(property.Value, "Barangay", out barangay, out error))
                        return false;
                    break;
                case "municipality":
                    if (!TryReadAreaName(property.Value, "Municipality", out municipality, out error))
                        return false;
                    break;
                case "city":
                    if (!TryReadAreaName(property.Value, "City", out city, out error))
                        return false;
                    break;
                case "province":
                    if (!TryReadAreaName(property.Value, "Province", out province, out error))
                        return false;
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

        if (radiusKm is null)
        {
            error = "RadiusKm is required.";
            return false;
        }

        if (radiusKm is < MinRadiusKm or > MaxRadiusKm)
        {
            error = $"RadiusKm must be between {MinRadiusKm} and {MaxRadiusKm}.";
            return false;
        }

        request = new FuelPriceSearchRequest(
            latitude.Value,
            longitude.Value,
            radiusKm.Value,
            barangay,
            municipality,
            city,
            province);
        return true;
    }

    private static bool TryReadFiniteNumber(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out number) &&
               double.IsFinite(number);
    }

    private static bool TryReadAreaName(
        JsonElement value,
        string displayName,
        out string? areaName,
        out string error)
    {
        areaName = null;
        error = "";

        if (value.ValueKind == JsonValueKind.Null)
            return true;

        if (value.ValueKind != JsonValueKind.String)
        {
            error = $"{displayName} must be a string or null.";
            return false;
        }

        areaName = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(areaName))
        {
            areaName = null;
            return true;
        }

        if (areaName.Length > MaxAreaNameLength)
        {
            error = $"{displayName} must not exceed {MaxAreaNameLength} characters.";
            return false;
        }

        return true;
    }
}
