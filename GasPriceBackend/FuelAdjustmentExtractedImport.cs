using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;

public sealed record FuelAdjustmentExtractedRow(
    [property: JsonRequired] string OilCompany,
    [property: JsonRequired] string EffectiveDatePhilippines,
    [property: JsonRequired] string? EffectiveTimePhilippines,
    [property: JsonRequired] decimal? GasolineChangePerLiter,
    [property: JsonRequired] decimal? DieselChangePerLiter,
    [property: JsonRequired] decimal? KeroseneChangePerLiter);

public sealed record FuelAdjustmentExtractedImportRequest(
    [property: JsonRequired] string SourceUrl,
    [property: JsonRequired] string WeekStart,
    [property: JsonRequired] string WeekEnd,
    [property: JsonRequired] IReadOnlyList<FuelAdjustmentExtractedRow> Rows);

public static class FuelAdjustmentExtractedImportEndpoints
{
    private const int MaxBodyBytes = 128 * 1024;
    private const string BodySizeError = "Extracted adjustment body must not exceed 128 KiB.";
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static void MapFuelAdjustmentExtractedImportEndpoints(this WebApplication app, string instanceId)
    {
        app.MapPost(ApiRoutes.AdminFuelPriceAdjustmentsImportExtracted, async (
            HttpRequest request, AppDbContext db, TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (request.Query.Count != 0)
                return ApiResults.BadRequest("This endpoint accepts a JSON body and no query parameters.", instanceId);
            if (!request.HasJsonContentType())
                return ApiResults.BadRequest("Content-Type must be application/json.", instanceId);
            try
            {
                var bodySize = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (bodySize is { IsReadOnly: false }) bodySize.MaxRequestBodySize = MaxBodyBytes;
                if (request.ContentLength > MaxBodyBytes)
                    return ApiResults.BadRequest(BodySizeError, instanceId);
                using var body = new MemoryStream();
                var buffer = new byte[8192];
                int count;
                while ((count = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    // Also bound chunked requests and hosts without a body-size feature.
                    if (body.Length + count > MaxBodyBytes)
                        return ApiResults.BadRequest(BodySizeError, instanceId);
                    await body.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                using var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
                RejectDuplicateProperties(document.RootElement);
                var payload = document.RootElement.Deserialize<FuelAdjustmentExtractedImportRequest>(InputJson)
                    ?? throw new InvalidDataException("Provide an extracted fuel-adjustment notice object.");
                if (string.IsNullOrWhiteSpace(payload.SourceUrl))
                    throw new InvalidDataException("sourceUrl must be a DOE fuel-adjustment PDF URL.");
                string url;
                try { url = FuelAdjustmentImporter.ValidatePdfUrl(payload.SourceUrl); }
                catch (ArgumentException ex) { throw new InvalidDataException(ex.Message); }
                if (payload.Rows is null or { Count: < 1 or > 100 } || payload.Rows.Any(row => row is null))
                    throw new InvalidDataException("Provide between 1 and 100 company adjustment rows.");
                var extraction = new FuelAdjustmentExtraction(payload.WeekStart, payload.WeekEnd,
                    payload.Rows.Select(row => new ExtractedFuelAdjustment(row.OilCompany,
                        row.EffectiveDatePhilippines, row.EffectiveTimePhilippines,
                        row.GasolineChangePerLiter, row.DieselChangePerLiter, row.KeroseneChangePerLiter)).ToArray());
                var result = await FuelAdjustmentEndpoints.ImportExtractionAsync(db, timeProvider, url,
                    extraction, null, null, cancellationToken);
                return ApiResults.Ok(result!, $"fuel_adjustments_{result!.Status}", instanceId);
            }
            catch (JsonException)
            {
                return ApiResults.BadRequest("Provide valid JSON with the documented fields and value types.", instanceId);
            }
            catch (InvalidDataException ex)
            {
                return ApiResults.BadRequest(ex.Message, instanceId);
            }
            catch (FuelAdjustmentExtractionException ex)
            {
                return ApiResults.BadRequest(ex.Message, instanceId);
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return ApiResults.BadRequest(BodySizeError, instanceId);
            }
        });
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
