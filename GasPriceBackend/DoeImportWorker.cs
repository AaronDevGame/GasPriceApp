using System.Text.Json;
using Microsoft.EntityFrameworkCore;

public sealed record DoeImportReportStatus(string Section, string? Subdivision,
    DateOnly? WeekStart, string SourceUrl, string Status, int Added, int Updated,
    string? Error)
{
    public int DuplicatesIgnored { get; init; }
    public int AggregateRowsIgnored { get; init; }
}

public sealed record DoeImportJobStatus(Guid JobId, string Mode, DateOnly? From,
    DateOnly? To, string Status, DateTime CreatedAtUtc, DateTime? StartedAtUtc,
    DateTime? FinishedAtUtc, int ReportsFound, int ReportsImported,
    int ReportsSkipped, int ReportsFailed, int PriceRowsAdded, int PriceRowsUpdated,
    IReadOnlyList<DoeImportReportStatus> Reports, string? Error);

public sealed class DoeImportWorker(IServiceScopeFactory scopes,
    TimeProvider timeProvider, ILogger<DoeImportWorker> logger) : BackgroundService
{
    private const int MaxReportsPerJob = 120;
    private static readonly TimeSpan ScheduleInterval = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    public static DoeImportJobStatus ToStatus(DoeImportJob job) => new(
        job.Id, job.Mode, job.From, job.To, job.Status, job.CreatedAtUtc,
        job.StartedAtUtc, job.FinishedAtUtc, job.ReportsFound,
        job.ReportsImported, job.ReportsSkipped, job.ReportsFailed,
        job.PriceRowsAdded, job.PriceRowsUpdated,
        JsonSerializer.Deserialize<DoeImportReportStatus[]>(job.DetailsJson) ?? [], job.Error);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The database retains jobs across restarts. A stale running job can be retried
        // after its worker has stopped; a live worker updates it after each report.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverStaleAsync(stoppingToken);
                await ScheduleLatestAsync(stoppingToken);
                await ProcessQueuedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "DOE bulk import worker loop failed.");
            }
            try { await _wake.WaitAsync(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task RecoverStaleAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-30);
        var stale = await db.DoeImportJobs.Where(j => j.Status == "running" &&
            j.HeartbeatAtUtc < cutoff && j.ActiveSlot == 1).ToListAsync(cancellationToken);
        foreach (var job in stale)
        {
            job.Status = "interrupted";
            job.ActiveSlot = null;
            job.FinishedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            job.Error = "The import stopped before completion. Start a new job to retry.";
        }
        if (stale.Count > 0) await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ScheduleLatestAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var importer = scope.ServiceProvider.GetRequiredService<DoeFuelPriceImporter>();
        if (!importer.IsConfigured) return;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (await db.DoeImportJobs.AsNoTracking().AnyAsync(j =>
            j.ActiveSlot == 1 ||
            (j.Mode == "latest" && j.CreatedAtUtc > now - ScheduleInterval),
            cancellationToken)) return;
        db.DoeImportJobs.Add(new DoeImportJob
        {
            Id = Guid.NewGuid(), Mode = "latest", Status = "queued",
            ActiveSlot = 1, CreatedAtUtc = now
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { /* Another instance scheduled the active slot. */ }
    }

    private async Task ProcessQueuedAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queued = await db.DoeImportJobs.AsNoTracking()
            .Where(j => j.Status == "queued")
            .OrderBy(j => j.CreatedAtUtc).Select(j => j.Id)
            .ToListAsync(cancellationToken);
        foreach (var id in queued)
        {
            var claimed = await db.DoeImportJobs.Where(j => j.Id == id && j.Status == "queued")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, "running")
                    .SetProperty(j => j.StartedAtUtc, timeProvider.GetUtcNow().UtcDateTime)
                    .SetProperty(j => j.HeartbeatAtUtc, timeProvider.GetUtcNow().UtcDateTime),
                    cancellationToken);
            if (claimed == 0) continue;
            await RunJobAsync(id, cancellationToken);
        }
    }

    private async Task RunJobAsync(Guid id, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var importer = scope.ServiceProvider.GetRequiredService<DoeFuelPriceImporter>();
        var job = await db.DoeImportJobs.SingleAsync(j => j.Id == id, cancellationToken);
        var results = new List<DoeImportReportStatus>();
        try
        {
            var discovery = await importer.FindReportsAsync(job.From, job.To,
                timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            var reports = discovery.Reports;
            foreach (var error in discovery.Errors)
            {
                results.Add(new DoeImportReportStatus(error.Section, null,
                    null, error.Url, "failed", 0, 0, error.Error));
                job.ReportsFailed++;
            }
            job.DetailsJson = JsonSerializer.Serialize(results);
            if (reports.Count == 0)
                throw new InvalidDataException("No DOE pump price PDFs were found for this range.");
            if (reports.Count > MaxReportsPerJob)
                throw new InvalidDataException(
                    $"Found {reports.Count} PDFs. Narrow the date range to at most {MaxReportsPerJob} reports.");
            job.ReportsFound = reports.Count;
            await db.SaveChangesAsync(cancellationToken);
            foreach (var source in reports)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DoeImportReportStatus item;
                try
                {
                    using var reportScope = scopes.CreateScope();
                    var reportDb = reportScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var reportImporter = reportScope.ServiceProvider.GetRequiredService<DoeFuelPriceImporter>();
                    var result = await reportImporter.ImportReportAsync(reportDb, source,
                        timeProvider.GetUtcNow().UtcDateTime, job.From, job.To,
                        job.Mode == "latest", cancellationToken);
                    item = new(source.Section, source.Subdivision, source.WeekStart,
                        source.Url, result.Status, result.Added, result.Updated, null)
                    {
                        DuplicatesIgnored = result.DuplicatesIgnored,
                        AggregateRowsIgnored = result.AggregateRowsIgnored
                    };
                    if (result.Status is "already_imported" or "outside_range") job.ReportsSkipped++;
                    else job.ReportsImported++;
                    job.PriceRowsAdded += result.Added;
                    job.PriceRowsUpdated += result.Updated;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or
                    JsonException or TaskCanceledException or DbUpdateException)
                {
                    logger.LogWarning(ex, "DOE report import failed for {Section} {WeekStart}.",
                        source.Section, source.WeekStart);
                    item = new(source.Section, source.Subdivision, source.WeekStart,
                        source.Url, "failed", 0, 0, SafeError(ex));
                    job.ReportsFailed++;
                }
                results.Add(item);
                job.DetailsJson = JsonSerializer.Serialize(results);
                // Also serves as a heartbeat for stale-job recovery.
                job.HeartbeatAtUtc = timeProvider.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(cancellationToken);
            }
            if (job.Mode == "latest")
            {
                var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                    timeProvider.GetUtcNow().UtcDateTime,
                    TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila")));
                foreach (var source in reports.Where(r => r.WeekStart.AddDays(13) < localToday))
                {
                    results.Add(new DoeImportReportStatus(source.Section, source.Subdivision,
                        source.WeekStart, source.Url, "stale", 0, 0,
                        "DOE has not published a report recent enough for the public price feed."));
                    job.ReportsFailed++;
                }
                var expected = new[]
                {
                    ("ncr-pump-prices", (string?)null),
                    ("north-luzon-pump-prices", (string?)null),
                    ("south-luzon-pump-prices", "Calabarzon"),
                    ("south-luzon-pump-prices", "Mimaropa"),
                    ("south-luzon-pump-prices", "Bicol"),
                    ("visayas-pump-prices", (string?)null),
                    ("mindanao-pump-prices", (string?)null)
                };
                foreach (var (section, subdivision) in expected)
                {
                    if (reports.Any(r => r.Section == section && r.Subdivision == subdivision) ||
                        discovery.Errors.Any(e => e.Section == section))
                        continue;
                    results.Add(new DoeImportReportStatus(section, subdivision,
                        null, "", "missing", 0, 0,
                        "No dated DOE PDF was found for this report group."));
                    job.ReportsFailed++;
                }
                job.DetailsJson = JsonSerializer.Serialize(results);
            }
            job.Status = job.ReportsFailed == 0 ? "completed" : "completed_with_gaps";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            job.Status = "interrupted";
            job.Error = "Server stopped during import.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DOE import job {JobId} failed.", id);
            job.Status = "failed";
            job.Error = SafeError(ex);
        }
        finally
        {
            job.ActiveSlot = null;
            job.FinishedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private static string SafeError(Exception ex) => ex switch
    {
        TaskCanceledException => "A DOE or extraction request timed out.",
        HttpRequestException => "DOE or extraction service was unavailable.",
        JsonException => "The extractor returned malformed JSON.",
        DbUpdateException => "The database could not save the report.",
        InvalidDataException => ex.Message,
        _ => "The DOE import failed."
    };
}
