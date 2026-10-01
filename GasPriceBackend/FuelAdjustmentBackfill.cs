using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;

public sealed record FuelAdjustmentBackfillNotice(
    string SourceUrl, string Status, FuelAdjustmentImportResult? Import, string? Error);

public sealed record FuelAdjustmentBackfillStatus(
    Guid JobId, DateOnly From, DateOnly To, string Status,
    DateTime CreatedAtUtc, DateTime? FinishedAtUtc,
    int TotalNotices,
    IReadOnlyList<FuelAdjustmentBackfillNotice> Notices,
    IReadOnlyList<DateOnly> MissingWeeks, string? Error);

public sealed class FuelAdjustmentBackfill : BackgroundService
{
    private const int MaxNotices = 60;
    private const int MaxRetainedJobs = 20;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, FuelAdjustmentBackfillStatus> _jobs = [];
    private readonly Dictionary<Guid, string[]> _suppliedUrls = [];
    private Guid? _active;

    public FuelAdjustmentBackfill(IServiceScopeFactory scopes, TimeProvider timeProvider)
    {
        _scopes = scopes;
        _timeProvider = timeProvider;
    }

    public bool TryStart(DateOnly from, DateOnly to, string[] urls,
        out FuelAdjustmentBackfillStatus status)
    {
        lock (_gate)
        {
            if (_active.HasValue)
            {
                status = _jobs[_active.Value];
                return false;
            }
            if (_jobs.Count >= MaxRetainedJobs)
            {
                var oldest = _jobs.Values.OrderBy(s => s.CreatedAtUtc).First().JobId;
                _jobs.Remove(oldest);
            }
            var id = Guid.NewGuid();
            status = new FuelAdjustmentBackfillStatus(id, from, to, "queued",
                _timeProvider.GetUtcNow().UtcDateTime, null, 0, [], [], null);
            _jobs[id] = status;
            _suppliedUrls[id] = urls;
            _active = id;
            _queue.Writer.TryWrite(id);
            return true;
        }
    }

    public FuelAdjustmentBackfillStatus? Get(Guid id)
    {
        lock (_gate)
            return _jobs.GetValueOrDefault(id);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await RunAsync(id, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                Update(id, s => s with { Status = "interrupted", Error = "Server stopped." });
                break;
            }
            catch (Exception exception)
            {
                Update(id, s => s with { Status = "failed", Error = SafeError(exception) });
            }
            finally
            {
                lock (_gate)
                {
                    _jobs[id] = _jobs[id] with { FinishedAtUtc = _timeProvider.GetUtcNow().UtcDateTime };
                    _suppliedUrls.Remove(id);
                    _active = null;
                }
            }
        }
    }

    private async Task RunAsync(Guid id, CancellationToken cancellationToken)
    {
        Update(id, s => s with { Status = "running" });
        using var scope = _scopes.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<FuelAdjustmentImporter>();
        var job = Get(id)!;
        var urls = new HashSet<string>(_suppliedUrls[id], StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var url in await importer.FindNoticeUrlsAsync(cancellationToken))
            {
                if (FuelAdjustmentNoticeName.MayOverlap(url, job.From, job.To))
                    urls.Add(url);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException)
        {
            if (urls.Count == 0) throw;
            Update(id, s => s with { Error = "DOE notice listing was unavailable; supplied URLs were used." });
        }
        if (urls.Count > MaxNotices)
            throw new InvalidDataException($"Found {urls.Count} PDFs; the limit is {MaxNotices}. Narrow the request or supplied URLs.");
        Update(id, s => s with { TotalNotices = urls.Count });

        var notices = new List<FuelAdjustmentBackfillNotice>();
        foreach (var url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FuelAdjustmentBackfillNotice notice;
            try
            {
                using var noticeScope = _scopes.CreateScope();
                var noticeDb = noticeScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var previous = await noticeDb.FuelAdjustments.AsNoTracking()
                    .Where(a => a.SourceUrl == url)
                    .Select(a => new { a.WeekStart, a.WeekEnd })
                    .FirstOrDefaultAsync(cancellationToken);
                if (previous is not null)
                    notice = new(url,
                        previous.WeekStart <= job.To && previous.WeekEnd >= job.From
                            ? "already_imported" : "outside_range", null, null);
                else
                {
                    var noticeImporter = noticeScope.ServiceProvider.GetRequiredService<FuelAdjustmentImporter>();
                    var result = await FuelAdjustmentEndpoints.ImportOneAsync(noticeDb, noticeImporter,
                        _timeProvider, url, job.From, job.To, cancellationToken);
                    notice = result is null
                        ? new(url, "outside_range", null, null)
                        : new(url, "imported", result, null);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or
                InvalidDataException or FuelAdjustmentExtractionException or JsonException or
                OperationCanceledException or DbUpdateException)
            {
                notice = new(url, "failed", null, SafeError(exception));
            }
            notices.Add(notice);
            Update(id, s => s with { Notices = notices.ToArray() });
        }

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var covered = (await db.FuelAdjustments.AsNoTracking()
            .Where(a => a.WeekStart <= job.To && a.WeekEnd >= job.From)
            .Select(a => a.WeekStart).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var missing = new List<DateOnly>();
        var firstTuesday = job.From.AddDays(-((int)job.From.DayOfWeek + 5) % 7);
        for (var week = firstTuesday; week <= job.To; week = week.AddDays(7))
        {
            if (week.AddDays(6) >= job.From && !covered.Contains(week))
                missing.Add(week);
        }
        Update(id, s => s with
        {
            Status = notices.Any(n => n.Status == "failed") || missing.Count > 0
                ? "completed_with_gaps" : "completed",
            MissingWeeks = missing
        });
    }

    private void Update(Guid id, Func<FuelAdjustmentBackfillStatus, FuelAdjustmentBackfillStatus> change)
    {
        lock (_gate)
            _jobs[id] = change(_jobs[id]);
    }

    private static string SafeError(Exception exception) => exception switch
    {
        OperationCanceledException => "A source request timed out.",
        HttpRequestException => "The upstream source was unavailable.",
        JsonException => "The extractor returned malformed JSON.",
        DbUpdateException => "The database could not save this notice.",
        FuelAdjustmentExtractionException => exception.Message,
        InvalidDataException => exception.Message,
        _ => "The backfill failed."
    };

    public static async Task<IResult> StartRequestAsync(HttpRequest request,
        FuelAdjustmentBackfill jobs, FuelAdjustmentImporter importer,
        TimeProvider timeProvider, string instanceId,
        CancellationToken cancellationToken)
    {
        if (!importer.IsConfigured)
            return ApiResults.ServiceUnavailable("fuel_adjustment_extractor_not_configured",
                instanceId, "OPENAI_API_KEY is required to import DOE notices.");
        if (request.Query.Count != 2 || !request.Query.TryGetValue("from", out var fromText) ||
            !request.Query.TryGetValue("to", out var toText) ||
            fromText.Count != 1 || toText.Count != 1 ||
            !DateOnly.TryParseExact(fromText[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var from) ||
            !DateOnly.TryParseExact(toText[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var to) || from.DayNumber < 7 || from > to ||
            to.DayNumber - from.DayNumber > 365 ||
            to > DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime.AddDays(7)))
            return ApiResults.BadRequest("Supply from and to as yyyy-MM-dd, in order, within 366 days.", instanceId);

        var urls = new List<string>();
        if (request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding") ||
            request.ContentType is not null)
        {
            if (!request.HasJsonContentType())
                return ApiResults.BadRequest("The request body must be application/json.", instanceId);
            if (request.ContentLength is > 65536)
                return ApiResults.BadRequest("The request body is too large.", instanceId);
            var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
                sizeFeature.MaxRequestBodySize = 65536;
            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body,
                    new JsonDocumentOptions { MaxDepth = 4 }, cancellationToken);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
                var foundUrls = false;
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Name != "sourceUrls" || foundUrls ||
                        property.Value.ValueKind != JsonValueKind.Array)
                        throw new JsonException();
                    foundUrls = true;
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String || urls.Count >= MaxNotices)
                            throw new JsonException();
                        urls.Add(FuelAdjustmentImporter.ValidatePdfUrl(item.GetString()!));
                    }
                }
                if (!foundUrls) throw new JsonException();
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                return ApiResults.BadRequest("Expected only sourceUrls: an array of up to 60 allowed DOE PDF URLs.", instanceId);
            }
        }

        if (!jobs.TryStart(from, to, urls.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), out var status))
            return ApiResults.TooManyRequest("A fuel-adjustment backfill is already running.");
        return Results.Json(new ApiResponse<FuelAdjustmentBackfillStatus>
        {
            Code = StatusCodes.Status202Accepted,
            Message = "fuel_adjustment_backfill_queued",
            InstanceId = instanceId,
            Data = status
        }, statusCode: StatusCodes.Status202Accepted);
    }
}

internal static class FuelAdjustmentNoticeName
{
    private const string Month = "Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?";
    private static readonly Regex MonthFirst = new(
        $@"\b(?<month1>{Month})\s*(?<day1>\d{{1,2}})\s*[-–]\s*(?:(?<month2>{Month})\s*)?(?<day2>\d{{1,2}})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DayFirst = new(
        $@"\b(?<day1>\d{{1,2}})\s*[-–]\s*(?<day2>\d{{1,2}})\s*(?<month1>{Month})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Year = new(@"\b20\d{2}\b", RegexOptions.Compiled);

    // A name is only a hint. If it is ambiguous, let PDF extraction decide.
    public static bool MayOverlap(string url, DateOnly from, DateOnly to)
    {
        var name = Uri.UnescapeDataString(new Uri(url).AbsolutePath.Split('/').Last());
        var match = MonthFirst.Match(name);
        if (!match.Success) match = DayFirst.Match(name);
        if (!match.Success) return true;

        var startMonth = ParseMonth(match.Groups["month1"].Value);
        var endMonth = match.Groups["month2"].Success
            ? ParseMonth(match.Groups["month2"].Value) : startMonth;
        var startDay = int.Parse(match.Groups["day1"].Value, CultureInfo.InvariantCulture);
        var endDay = int.Parse(match.Groups["day2"].Value, CultureInfo.InvariantCulture);
        if (startMonth == 0 || endMonth == 0) return true;

        var yearMatch = Year.Match(name);
        var years = yearMatch.Success
            ? new[] { int.Parse(yearMatch.Value, CultureInfo.InvariantCulture) - 1,
                int.Parse(yearMatch.Value, CultureInfo.InvariantCulture) }
            : Enumerable.Range(Math.Max(1, from.Year - 1), to.Year - from.Year + 3);
        var foundWeek = false;
        foreach (var year in years)
        {
            if (!DateOnly.TryParseExact($"{year:D4}-{startMonth:D2}-{startDay:D2}", "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                continue;
            var endYear = endMonth < startMonth ? year + 1 : year;
            if (!DateOnly.TryParseExact($"{endYear:D4}-{endMonth:D2}-{endDay:D2}", "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) ||
                end.DayNumber - start.DayNumber != 6 || start.DayOfWeek != DayOfWeek.Tuesday)
                continue;
            foundWeek = true;
            if (start <= to && end >= from)
                return true;
        }
        return !foundWeek;
    }

    private static int ParseMonth(string value)
    {
        var abbreviation = value[..Math.Min(3, value.Length)];
        for (var month = 1; month <= 12; month++)
        {
            if (abbreviation.Equals(CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month),
                    StringComparison.OrdinalIgnoreCase))
                return month;
        }
        return 0;
    }
}
