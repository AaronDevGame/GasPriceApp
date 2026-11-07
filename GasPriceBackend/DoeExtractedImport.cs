using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

public sealed record DoeExtractedPriceRow(
    [property: JsonRequired] string City,
    [property: JsonRequired] string Province,
    [property: JsonRequired] string OilCompany,
    [property: JsonRequired] string FuelGrade,
    [property: JsonRequired] decimal MinPricePerLiter,
    [property: JsonRequired] decimal MaxPricePerLiter)
{
    public string? Region { get; init; }
    public DateOnly? MonitoringStart { get; init; }
    public DateOnly? MonitoringEnd { get; init; }

    public ExtractedDoeBulkPrice ToBulkRow() => new(City, Province, Region, OilCompany,
        FuelGrade, MinPricePerLiter, MaxPricePerLiter)
    {
        MonitoringStart = MonitoringStart, MonitoringEnd = MonitoringEnd
    };
}

public sealed record DoeExtractedImportRequest(
    [property: JsonRequired] string SourceUrl,
    [property: JsonRequired] DateOnly WeekStart,
    [property: JsonRequired] DateOnly WeekEnd,
    [property: JsonRequired] bool Complete,
    [property: JsonRequired] IReadOnlyList<DoeExtractedPriceRow> Rows)
{
    public string CoverageSource { get; init; } = "report";
}

public static class DoeExtractedImportEndpoints
{
    private const int MaxBodyBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static void MapDoeExtractedImportEndpoints(this WebApplication app, string instanceId)
    {
        app.MapGet(ApiRoutes.AdminDoeExtractedReports, async (
            HttpRequest request, AppDbContext db, CancellationToken cancellationToken) =>
        {
            if (request.Query.Count != 1 || !request.Query.TryGetValue("sourceUrl", out var values) ||
                values.Count != 1 || values[0] is not { Length: > 0 and <= 2048 } sourceUrl)
                return ApiResults.BadRequest("Provide sourceUrl once, as a DOE PDF URL.", instanceId);
            try { sourceUrl = FuelAdjustmentImporter.ValidatePdfUrl(sourceUrl); }
            catch (ArgumentException)
            { return ApiResults.BadRequest("sourceUrl must be a DOE PDF URL.", instanceId); }
            // Return exact coverage instead of assuming listing dates equal printed PDF dates.
            var reports = await db.DoePumpPriceReports.AsNoTracking()
                .Where(r => r.SourceUrl == sourceUrl)
                .OrderByDescending(r => r.WeekStart)
                .Select(r => new { r.Id, r.WeekStart, r.WeekEnd, r.CoverageSource,
                    r.Section, r.Subdivision, r.Status, r.PriceRows, r.ImportedAtUtc })
                .ToArrayAsync(cancellationToken);
            return ApiResults.Ok(new { SourceUrl = sourceUrl, Reports = reports },
                "doe_extracted_reports", instanceId);
        });

        app.MapGet(ApiRoutes.AdminDoeExtractedJob, async (
            string jobId, AppDbContext db, CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(jobId, out var id))
                return ApiResults.BadRequest("jobId must be a UUID.", instanceId);
            var job = await db.DoeImportJobs.AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == id && j.Mode == "extracted", cancellationToken);
            if (job is null)
                return ApiResults.NotFound("Extracted DOE import job was not found.", instanceId,
                    ApiRoutes.AdminDoeExtractedJob);
            return ApiResults.Ok(DoeImportWorker.ToStatus(job), "doe_import_job", instanceId);
        });

        app.MapPost(ApiRoutes.AdminDoeFuelPricesImportExtracted, async (
            HttpRequest request, AppDbContext db, DoeImportWorker worker,
            TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            if (!request.HasJsonContentType())
                return ApiResults.BadRequest("Content-Type must be application/json.", instanceId);
            try
            {
                var bodySize = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (bodySize is { IsReadOnly: false }) bodySize.MaxRequestBodySize = MaxBodyBytes;
                // Enforce the bound even for chunked requests and test hosts without that feature.
                if (request.ContentLength > MaxBodyBytes)
                    return ApiResults.BadRequest("Extracted import body must not exceed 2 MiB.", instanceId);
                using var body = new MemoryStream();
                var buffer = new byte[16 * 1024];
                int count;
                while ((count = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    if (body.Length + count > MaxBodyBytes)
                        return ApiResults.BadRequest("Extracted import body must not exceed 2 MiB.", instanceId);
                    await body.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                using var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
                RejectDuplicateProperties(document.RootElement);
                var payload = document.RootElement.Deserialize<DoeExtractedImportRequest>(InputJson)
                    ?? throw new InvalidDataException("Provide an extracted DOE report object.");
                DoeFuelPriceImporter.ValidateSubmittedReport(payload, timeProvider.GetUtcNow().UtcDateTime);
                var job = new DoeImportJob
                {
                    Id = Guid.NewGuid(), Mode = "extracted", Status = "queued",
                    CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
                    RequestJson = JsonSerializer.Serialize(payload)
                };
                db.DoeImportJobs.Add(job);
                await db.SaveChangesAsync(cancellationToken);
                worker.Wake();
                return DoeImportEndpoints.Accepted(request, job, instanceId);
            }
            catch (JsonException)
            {
                return ApiResults.BadRequest("Provide valid JSON with the documented fields and value types.", instanceId);
            }
            catch (InvalidDataException ex)
            {
                return ApiResults.BadRequest(ex.Message, instanceId);
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return ApiResults.BadRequest("Extracted import body must not exceed 2 MiB.", instanceId);
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

public sealed partial class DoeFuelPriceImporter
{
    public static ValidatedDoeBulkRows ValidateSubmittedReport(DoeExtractedImportRequest request, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(request.SourceUrl))
            throw new InvalidDataException("sourceUrl must be a DOE PDF URL.");
        try { FuelAdjustmentImporter.ValidatePdfUrl(request.SourceUrl); }
        catch (ArgumentException)
        { throw new InvalidDataException("sourceUrl must be a DOE PDF URL."); }
        ValidateReportCoverage(request.WeekStart, request.WeekEnd, nowUtc);
        if (request.CoverageSource is not ("report" or "monitoring"))
            throw new InvalidDataException("coverageSource must be report or monitoring.");
        if (request.Rows is null or { Count: < 1 or > 4000 } || request.Rows.Any(row => row is null))
            throw new InvalidDataException("Provide between 1 and 4000 price rows.");
        foreach (var row in request.Rows)
        {
            ValidateMonitoringDates(row.MonitoringStart, row.MonitoringEnd, nowUtc);
            if (row.MinPricePerLiter > row.MaxPricePerLiter)
                throw new InvalidDataException("minPricePerLiter must not exceed maxPricePerLiter.");
        }
        var validated = ValidateBulkRows(request.Rows.Select(row => row.ToBulkRow()).ToArray());
        if (validated.AggregateRowsIgnored > 0)
            throw new InvalidDataException("Submit named company prices only; omit independent/common/overall aggregates.");
        // Conflicting monitoring metadata must not silently choose the first duplicate.
        if (request.Rows.GroupBy(row => BulkRowKey(row.City, row.Province, row.OilCompany, row.FuelGrade))
            .Any(group => group.Select(row => (row.MonitoringStart, row.MonitoringEnd)).Distinct().Count() > 1))
            throw new InvalidDataException("Duplicate price rows contain conflicting monitoring dates.");
        return validated;
    }

    public async Task<DoeReportImportResult> ImportSubmittedReportAsync(AppDbContext db,
        DoeExtractedImportRequest request, DateTime nowUtc, CancellationToken cancellationToken)
    {
        ResetExtractionUsage();
        var validated = ValidateSubmittedReport(request, nowUtc);
        ExtractionModel = "external:submitted";
        var url = FuelAdjustmentImporter.ValidatePdfUrl(request.SourceUrl);
        var locations = validated.Rows.Select(row =>
            new ResolvedFuelLocation(row.City, row.Province, row.Region)).ToArray();
        var sections = locations.Select(RegionSection).Distinct().ToArray();
        var subdivisions = locations.Select(SouthSubdivision).Distinct().ToArray();
        var source = new DoeReportSource(sections.Length == 1 ? sections[0] ?? "manual" : "manual",
            subdivisions.Length == 1 ? subdivisions[0] : null, request.WeekStart, url);
        var extraction = new DoeBulkExtraction(request.WeekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            request.WeekEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), validated.Rows)
            { CoverageSource = request.CoverageSource };
        // No PDF download or fabricated PDF hash/page cache for externally extracted rows.
        return await PersistReportAsync(db, source, extraction, validated, request.WeekStart,
            request.WeekEnd, null, nowUtc, 0, cancellationToken, request.Complete);
    }
}
