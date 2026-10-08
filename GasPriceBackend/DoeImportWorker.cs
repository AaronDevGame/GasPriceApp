using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

public sealed record DoeSingleImportRequest(string? City, string? Province, string? Region, string? SourceUrl);

public sealed record DoeImportReportStatus(string Section, string? Subdivision,
    DateOnly? WeekStart, string SourceUrl, string Status, int Added, int Updated,
    string? Error)
{
    public string? Model { get; init; }
    public AiChatTokenUsage? Usage { get; init; }
    public OpenAiCostEstimate? EstimatedCost { get; init; }
    public int DuplicatesIgnored { get; init; }
    public int AggregateRowsIgnored { get; init; }
    public DoeReportPageProgress? PageProgress { get; init; }
    public int RowsSkipped { get; init; }
    public IReadOnlyList<DoePriceRowError> RowErrors { get; init; } = [];
}

public sealed record DoeImportJobStatus(Guid JobId, string Mode, DateOnly? From,
    DateOnly? To, string Status, DateTime CreatedAtUtc, DateTime? StartedAtUtc,
    DateTime? FinishedAtUtc, int ReportsFound, int ReportsImported,
    int ReportsSkipped, int ReportsFailed, int PriceRowsAdded, int PriceRowsUpdated,
    IReadOnlyList<DoeImportReportStatus> Reports, string? Error)
{
    public string StatusUrl => ApiRoutes.AdminDoeFuelPricesImportJob.Replace("{jobId}", JobId.ToString());
    public DoeSingleImportRequest? Request { get; init; }
    public JsonElement? Result { get; init; }
    // Null means no recorded usage; historical jobs cannot be reconstructed.
    public AiChatTokenUsage? Usage => DoeImportBilling.SumUsage(Reports);
    public OpenAiCostEstimate? EstimatedCost => DoeImportBilling.SumCost(Reports);
}

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
        JsonSerializer.Deserialize<DoeImportReportStatus[]>(job.DetailsJson) ?? [], job.Error)
    {
        Request = job.RequestJson is null ? null : JsonSerializer.Deserialize<DoeSingleImportRequest>(job.RequestJson),
        Result = job.ResultJson is null ? null : JsonSerializer.Deserialize<JsonElement>(job.ResultJson)
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The database retains jobs across restarts. A stale running job can be retried
        // after its worker has stopped; live imports update their heartbeat during work.
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
                logger.LogError(ex, "DOE import worker loop failed.");
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
            var reports = JsonSerializer.Deserialize<DoeImportReportStatus[]>(job.DetailsJson) ?? [];
            job.DetailsJson = JsonSerializer.Serialize(reports.Select(report =>
                report.Status == "running"
                    ? report with { Status = "interrupted", Error = job.Error }
                    : report));
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
        // Serialize automatic scheduling across hosts now that queued jobs do not own the active slot.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(746302191)", cancellationToken);
        if (await db.DoeImportJobs.AsNoTracking().AnyAsync(j =>
            (j.Status == "queued" || j.Status == "running") ||
            (j.Mode == "latest" && j.CreatedAtUtc > now - ScheduleInterval),
            cancellationToken)) return;
        db.DoeImportJobs.Add(new DoeImportJob
        {
            Id = Guid.NewGuid(), Mode = "latest", Status = "queued",
            CreatedAtUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
            if (await db.DoeImportJobs.AsNoTracking().AnyAsync(
                j => j.ActiveSlot == 1 && j.Id != id, cancellationToken)) return;
            int claimed;
            try
            {
                claimed = await db.DoeImportJobs.Where(j => j.Id == id && j.Status == "queued")
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.ActiveSlot, (int?)1)
                        .SetProperty(j => j.Status, "running")
                        .SetProperty(j => j.StartedAtUtc, timeProvider.GetUtcNow().UtcDateTime)
                        .SetProperty(j => j.HeartbeatAtUtc, timeProvider.GetUtcNow().UtcDateTime),
                        cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Another host claimed the single database-backed worker slot.
                return;
            }
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
            if (job.Mode is "location" or "report")
            {
                await RunSingleImportAsync(job, db, importer, results, cancellationToken);
                return;
            }
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
                using var reportScope = scopes.CreateScope();
                var reportDb = reportScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var reportImporter = reportScope.ServiceProvider.GetRequiredService<DoeFuelPriceImporter>();
                var resultIndex = results.Count;
                results.Add(new(source.Section, source.Subdivision, source.WeekStart,
                    source.Url, "running", 0, 0, null));
                async Task SaveProgressAsync(DoeReportPageProgress? progress,
                    CancellationToken token)
                {
                    results[resultIndex] = results[resultIndex] with
                    {
                        PageProgress = progress,
                        Model = reportImporter.ExtractionModel,
                        Usage = reportImporter.ExtractionUsage,
                        EstimatedCost = reportImporter.ExtractionCost,
                        RowsSkipped = progress?.RowsSkipped ?? 0
                    };
                    job.DetailsJson = JsonSerializer.Serialize(results);
                    job.HeartbeatAtUtc = timeProvider.GetUtcNow().UtcDateTime;
                    await db.SaveChangesAsync(token);
                }
                await SaveProgressAsync(null, cancellationToken);
                try
                {
                    var result = await reportImporter.ImportReportAsync(reportDb, source,
                        timeProvider.GetUtcNow().UtcDateTime, job.From, job.To,
                        cancellationToken, (progress, token) => SaveProgressAsync(progress, token));
                    item = new(source.Section, source.Subdivision, source.WeekStart,
                        source.Url, result.Status, result.Added, result.Updated,
                        result.Status == "partial" ? $"Imported valid prices; skipped {result.RowsSkipped} invalid row(s)." : null)
                    {
                        Model = result.Model, Usage = result.Usage, EstimatedCost = result.EstimatedCost,
                        DuplicatesIgnored = result.DuplicatesIgnored,
                        AggregateRowsIgnored = result.AggregateRowsIgnored,
                        PageProgress = result.PageProgress, RowsSkipped = result.RowsSkipped,
                        RowErrors = result.RowErrors
                    };
                    if (result.Status == "partial") job.ReportsFailed++;
                    else if (result.Status is "already_imported" or "outside_range") job.ReportsSkipped++;
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
                        source.Url, "failed", 0, 0, SafeError(ex))
                    {
                        Model = reportImporter.ExtractionModel,
                        Usage = reportImporter.ExtractionUsage,
                        EstimatedCost = reportImporter.ExtractionCost,
                        PageProgress = reportImporter.ExtractionProgress,
                        RowsSkipped = reportImporter.ExtractionProgress?.RowsSkipped ?? 0
                    };
                    job.ReportsFailed++;
                }
                results[resultIndex] = item;
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
            for (var index = 0; index < results.Count; index++)
                if (results[index].Status == "running")
                    results[index] = results[index] with { Status = "interrupted", Error = job.Error };
            job.DetailsJson = JsonSerializer.Serialize(results);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DOE import job {JobId} failed.", id);
            job.Status = "failed";
            job.Error = SafeError(ex);
            job.ReportsFailed = Math.Max(1, job.ReportsFailed);
            for (var index = 0; index < results.Count; index++)
                if (results[index].Status == "running")
                    results[index] = results[index] with
                    {
                        Status = "failed", Error = job.Error,
                        Model = importer.ExtractionModel, Usage = importer.ExtractionUsage,
                        EstimatedCost = importer.ExtractionCost,
                        PageProgress = importer.ExtractionProgress
                    };
            job.DetailsJson = JsonSerializer.Serialize(results);
        }
        finally
        {
            job.ActiveSlot = null;
            job.FinishedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task RunSingleImportAsync(DoeImportJob job, AppDbContext db,
        DoeFuelPriceImporter importer, List<DoeImportReportStatus> results,
        CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<DoeSingleImportRequest>(job.RequestJson
            ?? throw new InvalidDataException("The import request is missing."))
            ?? throw new InvalidDataException("The import request is invalid.");
        job.ReportsFound = 1;
        results.Add(new("", null, null, request.SourceUrl ?? "", "running", 0, 0, null));
        job.DetailsJson = JsonSerializer.Serialize(results);
        await db.SaveChangesAsync(cancellationToken);
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = KeepAliveAsync(job.Id, heartbeatStop.Token);
        try
        {
            using var importScope = scopes.CreateScope();
            var importDb = importScope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (job.Mode == "report")
            {
                var result = await importer.ImportReportFromUrlAsync(importDb, request.SourceUrl!,
                    timeProvider.GetUtcNow().UtcDateTime, cancellationToken, async (progress, token) =>
                    {
                        results[0] = results[0] with
                        {
                            PageProgress = progress, Model = importer.ExtractionModel,
                            Usage = importer.ExtractionUsage, EstimatedCost = importer.ExtractionCost,
                            RowsSkipped = progress.RowsSkipped
                        };
                        job.DetailsJson = JsonSerializer.Serialize(results);
                        await db.SaveChangesAsync(token);
                    });
                job.ResultJson = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                results[0] = results[0] with
                {
                    SourceUrl = result.SourceUrl, Status = result.Status,
                    Added = result.Added, Updated = result.Updated,
                    Model = result.Model, Usage = result.Usage, EstimatedCost = result.EstimatedCost,
                    DuplicatesIgnored = result.DuplicatesIgnored, AggregateRowsIgnored = result.AggregateRowsIgnored,
                    PageProgress = result.PageProgress, RowsSkipped = result.RowsSkipped, RowErrors = result.RowErrors
                };
            }
            else
            {
                var result = await importer.ImportAsync(importDb,
                    new ResolvedFuelLocation(request.City!, request.Province!, request.Region),
                    timeProvider.GetUtcNow().UtcDateTime, request.SourceUrl, cancellationToken);
                job.ResultJson = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                results[0] = results[0] with
                {
                    SourceUrl = result.SourceUrl, WeekStart = result.WeekStart, Status = result.Status,
                    Added = result.Added, Updated = result.Updated,
                    Model = result.Model, Usage = result.Usage, EstimatedCost = result.EstimatedCost
                };
            }
            var report = results[0];
            job.PriceRowsAdded = report.Added;
            job.PriceRowsUpdated = report.Updated;
            if (report.Status == "partial") job.ReportsFailed = 1;
            else if (report.Status == "already_imported") job.ReportsSkipped = 1;
            else job.ReportsImported = 1;
            job.DetailsJson = JsonSerializer.Serialize(results);
            job.Status = job.ReportsFailed == 0 ? "completed" : "completed_with_gaps";
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            await heartbeat;
        }
    }

    private async Task KeepAliveAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.DoeImportJobs.Where(j => j.Id == id && j.Status == "running")
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.HeartbeatAtUtc,
                        timeProvider.GetUtcNow().UtcDateTime), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
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
