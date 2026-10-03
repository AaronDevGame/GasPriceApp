using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

public sealed record DoePriceHistoryRow(long Id, long? ReportId,
    DateOnly WeekStart, DateOnly WeekEnd, string City, string Province,
    string? Region, string OilCompany, string FuelGrade, decimal MinPricePerLiter,
    decimal MaxPricePerLiter, string SourceUrl, DateTime FetchedAtUtc);

public sealed record DoePriceHistoryPage(IReadOnlyList<DoePriceHistoryRow> Prices,
    string? NextCursor);

public static class DoeImportEndpoints
{
    public static void MapDoeImportEndpoints(this WebApplication app, string instanceId)
    {
        app.MapPost(ApiRoutes.AdminDoeFuelPricesImportJobs, async (
            HttpRequest request, AppDbContext db, DoeFuelPriceImporter importer,
            DoeImportWorker worker, TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!importer.IsConfigured)
                return ApiResults.ServiceUnavailable("doe_price_extractor_not_configured", instanceId);
            if (!request.HasJsonContentType() || request.ContentLength is > 4096)
                return ApiResults.BadRequest("Provide a small JSON request body.", instanceId);
            var bodySizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySizeFeature is { IsReadOnly: false })
                bodySizeFeature.MaxRequestBodySize = 4096;
            JsonDocument document;
            try { document = await JsonDocument.ParseAsync(request.Body,
                new JsonDocumentOptions { MaxDepth = 3 }, cancellationToken); }
            catch (JsonException)
            { return ApiResults.BadRequest("Request body must be valid JSON.", instanceId); }
            using var parsedDocument = document;
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1) ||
                root.EnumerateObject().Any(p => p.Name is not ("mode" or "from" or "to")) ||
                !root.TryGetProperty("mode", out var modeValue) ||
                modeValue.ValueKind != JsonValueKind.String)
                return ApiResults.BadRequest("Provide mode latest or backfill.", instanceId);
            var mode = modeValue.GetString();
            DateOnly? from = null;
            DateOnly? to = null;
            if (mode == "latest")
            {
                if (root.TryGetProperty("from", out _) || root.TryGetProperty("to", out _))
                    return ApiResults.BadRequest("Latest mode does not accept from or to.", instanceId);
            }
            else if (mode == "backfill")
            {
                if (!root.TryGetProperty("from", out var fromValue) ||
                    !root.TryGetProperty("to", out var toValue) ||
                    fromValue.ValueKind != JsonValueKind.String ||
                    toValue.ValueKind != JsonValueKind.String ||
                    !DateOnly.TryParseExact(fromValue.GetString(), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedFrom) ||
                    !DateOnly.TryParseExact(toValue.GetString(), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTo) ||
                    parsedFrom > parsedTo || parsedTo.DayNumber - parsedFrom.DayNumber > 90 ||
                    parsedTo > DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime.AddDays(7)))
                    return ApiResults.BadRequest(
                        "Provide inclusive from and to dates as yyyy-MM-dd, in order, within 91 days.", instanceId);
                from = parsedFrom;
                to = parsedTo;
            }
            else return ApiResults.BadRequest("mode must be latest or backfill.", instanceId);

            var active = await db.DoeImportJobs.AsNoTracking()
                .FirstOrDefaultAsync(j => j.ActiveSlot == 1, cancellationToken);
            if (active is not null)
                return ApiResults.BadRequest("A DOE import job is already active: " + active.Id, instanceId);
            var job = new DoeImportJob
            {
                Id = Guid.NewGuid(), Mode = mode, From = from, To = to,
                Status = "queued", ActiveSlot = 1,
                CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime
            };
            db.DoeImportJobs.Add(job);
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException)
            { return ApiResults.BadRequest("A DOE import job is already active.", instanceId); }
            worker.Wake();
            return Results.Json(new ApiResponse<DoeImportJobStatus>
            {
                Code = StatusCodes.Status202Accepted, Message = "doe_import_queued",
                InstanceId = instanceId, Data = DoeImportWorker.ToStatus(job)
            }, statusCode: StatusCodes.Status202Accepted);
        });

        app.MapGet(ApiRoutes.AdminDoeFuelPricesImportJob, async (
            string jobId, AppDbContext db, CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(jobId, out var id))
                return ApiResults.BadRequest("jobId must be a UUID.", instanceId);
            var job = await db.DoeImportJobs.AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
            return job is null
                ? ApiResults.NotFound("DOE import job was not found.", instanceId,
                    ApiRoutes.AdminDoeFuelPricesImportJob)
                : ApiResults.Ok(DoeImportWorker.ToStatus(job), "doe_import_job", instanceId);
        });

        app.MapGet(ApiRoutes.AdminDoeFuelPricesHistory, async (
            HttpRequest request, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal)
                { "from", "to", "city", "province", "region", "section", "limit", "cursor" };
            if (request.Query.Any(p => !allowed.Contains(p.Key) || p.Value.Count != 1))
                return ApiResults.BadRequest("Unknown or repeated history parameter.", instanceId);
            DateOnly? from = null;
            DateOnly? to = null;
            if (request.Query.TryGetValue("from", out var fromText))
            {
                if (!DateOnly.TryParseExact(fromText[0], "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return ApiResults.BadRequest("from must be yyyy-MM-dd.", instanceId);
                from = date;
            }
            if (request.Query.TryGetValue("to", out var toText))
            {
                if (!DateOnly.TryParseExact(toText[0], "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return ApiResults.BadRequest("to must be yyyy-MM-dd.", instanceId);
                to = date;
            }
            if (from > to)
                return ApiResults.BadRequest("from must be on or before to.", instanceId);
            var limit = 100;
            if (request.Query.TryGetValue("limit", out var limitText) &&
                (!int.TryParse(limitText[0], out limit) || limit is < 1 or > 500))
                return ApiResults.BadRequest("limit must be between 1 and 500.", instanceId);
            var city = Value("city");
            var province = Value("province");
            var region = Value("region");
            var section = Value("section");
            if (new[] { city, province, region, section }.Any(v => v is { Length: > 100 }))
                return ApiResults.BadRequest("Location filters must be at most 100 characters.", instanceId);
            if (new[] { city, province, region, section }.Any(v => v is not null && v.Length == 0))
                return ApiResults.BadRequest("Location filters cannot be empty.", instanceId);
            DateOnly? cursorWeek = null;
            long? cursorId = null;
            if (request.Query.TryGetValue("cursor", out var cursorText))
            {
                try
                {
                    if (cursorText[0] is null or { Length: > 128 }) throw new FormatException();
                    var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursorText[0]!)).Split('|');
                    if (parts.Length != 2 ||
                        !DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var week) ||
                        !long.TryParse(parts[1], NumberStyles.None,
                            CultureInfo.InvariantCulture, out var id) || id < 1)
                        throw new FormatException();
                    cursorWeek = week;
                    cursorId = id;
                }
                catch (FormatException)
                { return ApiResults.BadRequest("cursor is invalid.", instanceId); }
            }
            var query = db.DoeFuelPrices.AsNoTracking().AsQueryable();
            if (from.HasValue) query = query.Where(p => p.WeekEnd >= from.Value);
            if (to.HasValue) query = query.Where(p => p.WeekStart <= to.Value);
            if (city is not null) query = query.Where(p => p.CityKey == DoeFuelPriceImporter.Normalize(city));
            if (province is not null) query = query.Where(p => p.ProvinceKey == DoeFuelPriceImporter.Normalize(province));
            if (region is not null) query = query.Where(p => p.Region == region);
            if (section is not null)
                query = query.Where(p => p.ReportId != null && db.DoePumpPriceReports
                    .Any(r => r.Id == p.ReportId && r.Section == section));
            if (cursorWeek.HasValue && cursorId.HasValue)
                query = query.Where(p => p.WeekStart < cursorWeek.Value ||
                    (p.WeekStart == cursorWeek.Value && p.Id < cursorId.Value));
            var rows = await query.OrderByDescending(p => p.WeekStart)
                .ThenByDescending(p => p.Id).Take(limit + 1)
                .Select(p => new DoePriceHistoryRow(p.Id, p.ReportId, p.WeekStart,
                    p.WeekEnd, p.City, p.Province, p.Region, p.OilCompany, p.FuelGrade,
                    p.MinPricePerLiter, p.MaxPricePerLiter, p.SourceUrl, p.FetchedAtUtc))
                .ToListAsync(cancellationToken);
            var hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(rows.Count - 1);
            var last = rows.LastOrDefault();
            var next = hasMore && last is not null
                ? Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    last.WeekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" +
                    last.Id.ToString(CultureInfo.InvariantCulture)))
                : null;
            return ApiResults.Ok(new DoePriceHistoryPage(rows, next),
                "doe_price_history", instanceId);

            string? Value(string key) => request.Query.TryGetValue(key, out var values)
                ? values[0]?.Trim() : null;
        });
    }
}
