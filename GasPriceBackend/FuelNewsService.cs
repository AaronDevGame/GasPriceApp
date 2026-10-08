using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

public sealed class FuelNewsConflictException(string message) : Exception(message);

public sealed class FuelNewsService(AppDbContext db, TimeProvider clock)
{
    public async Task<FuelNewsImportResult> ImportAsync(FuelNewsImport request, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var error = FuelNewsValidation.Validate(request, now);
        if (error is not null) throw new InvalidDataException(error);
        var a = request.Article;
        // Canonical ordering prevents retries from becoming revisions merely because arrays were reordered.
        a = a with
        {
            Adjustments = a.Adjustments.OrderBy(c => c.Fuel, StringComparer.Ordinal)
                .ThenBy(c => c.OilCompany, StringComparer.Ordinal).ToArray(),
            Sources = a.Sources.OrderBy(s => s.Url, StringComparer.Ordinal).ToArray()
        };
        var json = JsonSerializer.Serialize(a, FuelNewsValidation.Json);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // One transaction serializes news imports across hosts, including forecast supersession and the push outbox.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(746302192)", ct);
        var item = await db.FuelNews.SingleOrDefaultAsync(n => n.ImportKey == a.ImportKey, ct);
        if (item?.ContentHash == hash)
            return new("already_imported", FuelNewsValidation.View(item, now));
        if (item is not null)
        {
            if (request.ExpectedRevision != item.Revision)
                throw new FuelNewsConflictException($"Fetch the current article and provide expectedRevision {item.Revision} before updating it.");
            if (item.TopicKey != a.TopicKey || item.Category != a.Category || item.Status != a.Status || item.SupersededById is not null)
                throw new InvalidDataException("An article's topic, category and status are immutable, and superseded forecasts cannot be edited. Use a new importKey for a confirmed announcement.");
        }
        else if (request.ExpectedRevision is not null)
            throw new FuelNewsConflictException("The article does not exist; expectedRevision must be null for a new import.");
        if (a.Status == "forecast" && await db.FuelNews.AnyAsync(n => n.TopicKey == a.TopicKey && n.Status == "confirmed", ct))
            throw new InvalidDataException("This topic already has a confirmed announcement; do not publish another forecast.");
        if (a.Status == "confirmed") await CheckDoeAsync(a, ct);
        var isNew = item is null;
        var oldArticle = item is null ? null : FuelNewsValidation.View(item, now).Article;
        item ??= new FuelNews { Id = Guid.NewGuid(), ImportKey = a.ImportKey, TopicKey = a.TopicKey,
            Category = a.Category, Status = a.Status, PublishedAtUtc = now };
        if (isNew) db.FuelNews.Add(item);
        item.ContentJson = json;
        item.ContentHash = hash;
        item.ExpiresAtUtc = a.ExpiresAtUtc;
        item.UpdatedAtUtc = now;
        item.Revision++;
        db.FuelNewsRevisions.Add(new FuelNewsRevision { NewsId = item.Id, Revision = item.Revision,
            ContentJson = json, SavedAtUtc = now });
        if (a.Status == "confirmed")
        {
            var forecasts = await db.FuelNews.Where(n => n.TopicKey == a.TopicKey && n.Status == "forecast" && n.SupersededById == null).ToListAsync(ct);
            foreach (var forecast in forecasts) forecast.SupersededById = item.Id;
        }
        // Text/source-only corrections do not cause another notification.
        var material = oldArticle is null || oldArticle.EffectiveDatePhilippines != a.EffectiveDatePhilippines ||
            oldArticle.EffectiveAtUtc != a.EffectiveAtUtc || !oldArticle.Adjustments.SequenceEqual(a.Adjustments);
        if (a.Category == "adjustment" && material)
        {
            var subscribers = await db.FuelNewsSubscriptions.AsNoTracking().Where(s => s.Enabled &&
                (a.Status == "confirmed" || s.IncludeForecasts)).ToListAsync(ct);
            foreach (var subscriber in subscribers)
                db.FuelNewsDeliveries.Add(new FuelNewsDelivery { Id = Guid.NewGuid(), NewsId = item.Id,
                    Revision = item.Revision, AppInstanceId = subscriber.AppInstanceId, Status = "queued",
                    NextAttemptAtUtc = now, CreatedAtUtc = now });
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(isNew ? "imported" : "updated", FuelNewsValidation.View(item, now));
    }

    private async Task CheckDoeAsync(FuelNewsContent article, CancellationToken ct)
    {
        var rows = await db.FuelAdjustments.AsNoTracking()
            .Where(a => a.EffectiveDatePhilippines == article.EffectiveDatePhilippines).ToListAsync(ct);
        foreach (var change in article.Adjustments)
        {
            foreach (var row in rows.Where(r => change.OilCompany is null ||
                         r.OilCompany.Equals(change.OilCompany, StringComparison.OrdinalIgnoreCase)))
            {
                var value = change.Fuel switch { "gasoline" => row.GasolineChangePerLiter,
                    "diesel" => row.DieselChangePerLiter, _ => row.KeroseneChangePerLiter };
                if (value is not null && value != change.MinChangePerLiter)
                    throw new InvalidDataException("A confirmed adjustment conflicts with stored DOE figures. Specify the correct company or reconcile the sources before importing.");
            }
        }
    }
}
