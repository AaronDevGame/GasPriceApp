using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

public static class DoeBenchmarkEndpoints
{
    public static void MapDoeBenchmarkEndpoints(this WebApplication app, string instanceId)
    {
        app.MapPost(ApiRoutes.AdminDoeFuelPricesBenchmark, async (
            HttpRequest request, AppDbContext db, DoeFuelPriceImporter importer,
            DoeImportWorker worker, TimeProvider timeProvider,
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
            var job = new DoeImportJob
            {
                Id = Guid.NewGuid(), Mode = "benchmark",
                CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
                RequestJson = JsonSerializer.Serialize(new DoeSingleImportRequest(null, null, null, url))
            };
            db.DoeImportJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
            worker.Wake();
            return DoeImportEndpoints.Accepted(request, job, instanceId, "doe_benchmark_queued");
        });

        app.MapGet(ApiRoutes.AdminDoeFuelPricesBenchmarkJob, async (
            string jobId, HttpResponse response, AppDbContext db, CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(jobId, out var id))
                return ApiResults.BadRequest("jobId must be a UUID.", instanceId);
            var job = await db.DoeImportJobs.AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == id && j.Mode == "benchmark", cancellationToken);
            return job is null
                ? ApiResults.NotFound("DOE benchmark job was not found.", instanceId,
                    ApiRoutes.AdminDoeFuelPricesBenchmarkJob)
                : ApiResults.Ok(DoeImportWorker.ToStatus(job), "doe_benchmark_job", instanceId);
        });
    }
}
