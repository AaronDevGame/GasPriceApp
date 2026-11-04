using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class FuelNewsMcpTools(AppDbContext db, FuelNewsService news, TimeProvider clock,
    IConfiguration config, IHttpContextAccessor http)
{
    [McpServerTool(Name = "list_news", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read Philippine fuel-news history, including expired/superseded articles. Does not research or publish news. Follow nextCursor for pagination.")]
    public async Task<FuelNewsPage> ListNews(CancellationToken ct,
        [Description("Article count, from 1 to 50.")] int limit = 20,
        [Description("Opaque nextCursor from the previous page, or null.")] string? cursor = null)
    {
        if (limit is < 1 or > 50) throw new McpException("limit must be 1–50.");
        var query = db.FuelNews.AsNoTracking();
        if (cursor is not null)
        {
            if (!FuelNewsEndpoints.TryCursor(cursor, out var date, out var id)) throw new McpException("Invalid news cursor.");
            query = query.Where(n => n.PublishedAtUtc < date || (n.PublishedAtUtc == date && n.Id.CompareTo(id) < 0));
        }
        var rows = await query.OrderByDescending(n => n.PublishedAtUtc).ThenByDescending(n => n.Id).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).ToArray();
        var now = clock.GetUtcNow().UtcDateTime;
        return new(items.Select(n => FuelNewsValidation.View(n, now)).ToArray(),
            rows.Count > limit ? FuelNewsEndpoints.Cursor(items[^1]) : null);
    }

    [McpServerTool(Name = "read_article", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read an article and its current revision before an update. Do not edit superseded forecasts.")]
    public async Task<FuelNewsView> ReadArticle([Description("Article UUID from list_news.")] string id, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "D", out var newsId)) throw new McpException("id must be a UUID.");
        var item = await db.FuelNews.AsNoTracking().SingleOrDefaultAsync(n => n.Id == newsId, ct);
        return item is null ? throw new McpException("fuel_news_not_found") : FuelNewsValidation.View(item, clock.GetUtcNow().UtcDateTime);
    }

    [Authorize(Policy = FuelNewsMcp.PublishPolicy)]
    [McpServerTool(Name = "publish_or_update", ReadOnly = false, Destructive = true, OpenWorld = true)]
    [Description("Publish real, researched Philippine fuel news to production. May notify subscribers. Read sources and verify timestamps first; never use sample news. For new articles expectedRevision must be null; for updates read the current revision first. Keep importKey, topicKey, category and status immutable. Never edit superseded forecasts. Forecast and confirmed announcements need separate importKeys and the same topicKey. Resolve validation and revision conflicts; never blindly retry.")]
    public async Task<FuelNewsImportResult> PublishOrUpdate(
        [Description("Complete article matching the fuel-news import contract. Explicitly include nullable fields. UTC timestamps must end in Z. Sources must be verified, dated, public HTTPS sources.")] FuelNewsContent article,
        [Description("Explicit null for a new article; current positive revision for an update.")] int? expectedRevision,
        CancellationToken ct)
    {
        // Enforce again at the operation boundary, including direct invocations.
        var context = http.HttpContext;
        if (context is null || !(await context.RequestServices.GetRequiredService<IAuthorizationService>()
                .AuthorizeAsync(context.User, null, FuelNewsMcp.PublishPolicy)).Succeeded)
            throw new McpException("Publishing requires an authorized publisher with fuel-news:read and fuel-news:publish scopes.");
        if (!config.GetValue<bool>("Mcp:PublishEnabled"))
            throw new McpException("MCP publishing is disabled. Enable Mcp:PublishEnabled only after verifying the OAuth connection and publisher permissions.");
        // Same atomic import, validation, revision checks and outbox. No key copy or HTTP hop.
        try { return await news.ImportAsync(new(article, expectedRevision), ct); }
        catch (InvalidDataException ex) { throw new McpException(ex.Message); }
        catch (FuelNewsConflictException ex) { throw new McpException("Revision conflict: " + ex.Message); }
    }
}
