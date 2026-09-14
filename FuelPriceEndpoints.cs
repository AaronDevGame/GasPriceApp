using System.Text.Json;

public sealed record FuelPriceSearchRequest(
    string? City,
    string? Province,
    string? Region);

public sealed record FuelPriceApiResponse(
    JsonElement Estimate,
    string RetrievalMethod);

public static class FuelPriceEndpoints
{
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
                        "ai_web_fallback"),
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
        request = new FuelPriceSearchRequest(null, null, null);
        error = "";

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "Fuel-price request must be a JSON object.";
            return false;
        }

        string? city = null;
        string? province = null;
        string? region = null;
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
                case "city":
                    if (!TryReadAreaName(property.Value, "City", out city, out error))
                        return false;
                    break;
                case "province":
                    if (!TryReadAreaName(property.Value, "Province", out province, out error))
                        return false;
                    break;
                case "region":
                    if (!TryReadAreaName(property.Value, "Region", out region, out error))
                        return false;
                    break;
                default:
                    error = $"Unknown fuel-price field '{property.Name}'.";
                    return false;
            }
        }

        if (city is null && province is null && region is null)
        {
            error = "At least one of city, province, or region is required.";
            return false;
        }

        request = new FuelPriceSearchRequest(city, province, region);
        return true;
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
