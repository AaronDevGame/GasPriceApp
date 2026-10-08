using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

public static class FuelNewsEndpoints
{
    public static void MapFuelNewsEndpoints(this WebApplication app, AuthService auth, string instanceId)
    {
        app.MapGet(ApiRoutes.FuelNewsLatest, async (HttpRequest request, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (request.Query.Count != 0) return ApiResults.BadRequest("This endpoint takes no query parameters.", instanceId);
            var now = clock.GetUtcNow().UtcDateTime;
            var item = await db.FuelNews.AsNoTracking().Where(n => n.ExpiresAtUtc > now && n.SupersededById == null)
                .OrderByDescending(n => n.PublishedAtUtc).ThenByDescending(n => n.Id).FirstOrDefaultAsync(ct);
            return ApiResults.Ok(item is null ? null : FuelNewsValidation.View(item, now), "fuel_news_latest", instanceId);
        });
        app.MapGet(ApiRoutes.FuelNews, async (HttpRequest request, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (request.Query.Any(q => q.Key is not ("limit" or "cursor") || q.Value.Count != 1))
                return ApiResults.BadRequest("Unknown or repeated news parameter.", instanceId);
            var limit = 20;
            if (request.Query.TryGetValue("limit", out var text) &&
                (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 50))
                return ApiResults.BadRequest("limit must be 1–50.", instanceId);
            var query = db.FuelNews.AsNoTracking();
            if (request.Query.TryGetValue("cursor", out var cursor))
            {
                if (!TryCursor(cursor.ToString(), out var date, out var id))
                    return ApiResults.BadRequest("Invalid news cursor.", instanceId);
                query = query.Where(n => n.PublishedAtUtc < date || (n.PublishedAtUtc == date && n.Id.CompareTo(id) < 0));
            }
            var rows = await query.OrderByDescending(n => n.PublishedAtUtc).ThenByDescending(n => n.Id).Take(limit + 1).ToListAsync(ct);
            var now = clock.GetUtcNow().UtcDateTime;
            var items = rows.Take(limit).ToArray();
            return ApiResults.Ok(new FuelNewsPage(items.Select(n => FuelNewsValidation.View(n, now)).ToArray(),
                rows.Count > limit ? Cursor(items[^1]) : null), "fuel_news", instanceId);
        });
        app.MapGet(ApiRoutes.FuelNewsDetail, async (string id, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!Guid.TryParseExact(id, "D", out var newsId)) return ApiResults.BadRequest("id must be a UUID.", instanceId);
            var item = await db.FuelNews.AsNoTracking().SingleOrDefaultAsync(n => n.Id == newsId, ct);
            return item is null ? ApiResults.NotFound("fuel_news_not_found", instanceId, ApiRoutes.FuelNewsDetail)
                : ApiResults.Ok(FuelNewsValidation.View(item, clock.GetUtcNow().UtcDateTime), "fuel_news_article", instanceId);
        });
        app.MapPost(ApiRoutes.AdminFuelNewsImport, async (HttpRequest request, FuelNewsService news, CancellationToken ct) =>
        {
            try
            {
                var body = await ReadJsonAsync<FuelNewsImport>(request, ct);
                var result = await news.ImportAsync(body, ct);
                return ApiResults.Ok(result, "fuel_news_" + result.Status, instanceId);
            }
            catch (InvalidDataException ex) { return ApiResults.BadRequest(ex.Message, instanceId); }
            catch (FuelNewsConflictException ex)
            {
                return Results.Json(new ApiResponse<object> { Code = 409, Message = "conflict", InstanceId = instanceId,
                    Error = new ApiError { Error = "news_revision_conflict", Detail = ex.Message } }, statusCode: 409);
            }
        });
        app.MapGet(ApiRoutes.AdminFuelNewsRevisions, async (string id, AppDbContext db, CancellationToken ct) =>
        {
            if (!Guid.TryParseExact(id, "D", out var newsId)) return ApiResults.BadRequest("id must be a UUID.", instanceId);
            var rows = await db.FuelNewsRevisions.AsNoTracking().Where(r => r.NewsId == newsId)
                .OrderByDescending(r => r.Revision).Take(100).ToListAsync(ct);
            return rows.Count == 0 ? ApiResults.NotFound("fuel_news_not_found", instanceId, ApiRoutes.AdminFuelNewsRevisions)
                : ApiResults.Ok(rows.Select(r => new { r.Revision, r.SavedAtUtc,
                    Article = JsonSerializer.Deserialize<FuelNewsContent>(r.ContentJson, FuelNewsValidation.Json) }), "fuel_news_revisions", instanceId);
        });
        app.MapFuelNewsSubscriptionEndpoints(auth, instanceId);
    }

    public static async Task<T> ReadJsonAsync<T>(HttpRequest request, CancellationToken ct)
    {
        const int maxBytes = 32768;
        if (!request.HasJsonContentType() || request.ContentLength is > maxBytes)
            throw new InvalidDataException("Provide an application/json body of at most 32 KiB.");
        // Bound chunked requests as well as requests with Content-Length.
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + read > maxBytes) throw new InvalidDataException("Request body exceeds 32 KiB.");
            buffer.Write(chunk, 0, read);
        }
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            if (FuelNewsValidation.HasDuplicateProperties(document.RootElement))
                throw new InvalidDataException("Duplicate JSON properties are not allowed.");
            return document.RootElement.Deserialize<T>(FuelNewsValidation.Json)
                ?? throw new InvalidDataException("Provide a JSON object.");
        }
        catch (JsonException) { throw new InvalidDataException("Malformed JSON, unknown properties, or incorrect field types."); }
    }

    private static string Cursor(FuelNews item) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        item.PublishedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + item.Id));
    private static bool TryCursor(string cursor, out DateTime date, out Guid id)
    {
        date = default; id = default;
        if (cursor.Length is < 1 or > 120) return false;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks || !Guid.TryParseExact(parts[1], "D", out id)) return false;
            date = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }
        catch (FormatException) { return false; }
    }
}
