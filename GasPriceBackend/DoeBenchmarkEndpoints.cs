using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

public static class DoeBenchmarkEndpoints
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static void MapDoeBenchmarkEndpoints(this WebApplication app, string instanceId)
    {
        app.MapPost(ApiRoutes.AdminDoeFuelPricesBenchmark, async (
            HttpRequest request, DoeFuelPriceImporter importer, TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasJsonContentType() || request.ContentLength is > 4096)
                return ApiResults.BadRequest("Provide a small JSON request body.", instanceId);
            var bodySize = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly: false }) bodySize.MaxRequestBodySize = 4096;
            string url;
            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body,
                    new JsonDocumentOptions { MaxDepth = 3 }, cancellationToken);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                    !root.TryGetProperty("sourceUrl", out var source) || source.ValueKind != JsonValueKind.String)
                    return ApiResults.BadRequest("Provide only sourceUrl as a DOE PDF URL.", instanceId);
                url = FuelAdjustmentImporter.ValidatePdfUrl(source.GetString()!);
            }
            catch (JsonException) { return ApiResults.BadRequest("Request body must be valid JSON.", instanceId); }
            catch (ArgumentException) { return ApiResults.BadRequest("sourceUrl must be a DOE PDF URL.", instanceId); }
            catch (BadHttpRequestException) { return ApiResults.BadRequest("Request body exceeds the size limit.", instanceId); }
            if (!importer.IsConfigured)
                return ApiResults.ServiceUnavailable("doe_price_extractor_not_configured", instanceId);
            if (!await Gate.WaitAsync(0, cancellationToken))
                return ApiResults.TooManyRequest("A DOE benchmark is already running.");
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                return ApiResults.Ok(await importer.BenchmarkAsync(url,
                    timeProvider.GetUtcNow().UtcDateTime, timeout.Token), "doe_extraction_benchmark", instanceId);
            }
            catch (InvalidDataException ex) { return ApiResults.BadRequest(ex.Message, instanceId); }
            catch (HttpRequestException) { return ApiResults.BadGateway("doe_pdf_unavailable", instanceId); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return ApiResults.GatewayTimeout("doe_benchmark_timeout", instanceId); }
            finally { Gate.Release(); }
        });
    }
}
