using System.Diagnostics;
using System.Security.Cryptography;

public sealed record DoeBenchmarkRun(string Status, string? Error, long ElapsedMilliseconds,
    string? Model, AiChatTokenUsage? Usage, OpenAiCostEstimate? EstimatedCost, int AiCalls,
    DoeBulkExtraction? Extraction, DoeReportPageProgress? PageProgress,
    IReadOnlyList<DoeLocalPageResult> LocalPages);

public sealed record DoeBenchmarkDifference(string Key, ExtractedDoeBulkPrice? Ai,
    ExtractedDoeBulkPrice? Hybrid);

public sealed record DoeBenchmarkComparison(int MatchingRows, bool DatesMatch,
    IReadOnlyList<DoeBenchmarkDifference> Differences, decimal? EstimatedApiSavingsUsd);

public sealed record DoeBenchmarkResult(string SourceUrl, string ContentHash, int PageCount,
    DateTime StartedAtUtc, long DownloadMilliseconds, DoeBenchmarkRun Ai,
    DoeBenchmarkRun Hybrid, DoeBenchmarkComparison? Comparison)
{
    public string AccuracyNote => "Agreement is not verified accuracy. Compare both outputs with manually checked source rows. Costs exclude backend CPU and are estimates; this benchmark bypasses extraction caches.";
}

public sealed partial class DoeFuelPriceImporter
{
    private Dictionary<int, DoePageExtraction>? _benchmarkLocalPages;
    private int _benchmarkAiRequests;

    public async Task<DoeBenchmarkResult> BenchmarkAsync(string sourceUrl, DateTime nowUtc,
        CancellationToken cancellationToken,
        Func<string, DoeBenchmarkRun, CancellationToken, Task>? progress = null)
    {
        var url = FuelAdjustmentImporter.ValidatePdfUrl(sourceUrl);
        var download = Stopwatch.StartNew();
        var pdf = await DownloadAsync(url, cancellationToken);
        download.Stop();
        using var document = new DoePdfPages(pdf);
        // Bound benchmark work before issuing any paid calls.
        if (document.Count > 4)
            throw new InvalidDataException("Benchmark accepts at most four pages. Use a smaller DOE report.");
        var ai = await RunBenchmarkAsync(pdf, nowUtc, false, cancellationToken, progress);
        var hybrid = await RunBenchmarkAsync(pdf, nowUtc, true, cancellationToken, progress);
        return new(url, Convert.ToHexString(SHA256.HashData(pdf)), document.Count, nowUtc,
            download.ElapsedMilliseconds, ai, hybrid, CompareBenchmark(ai, hybrid));
    }

    private async Task<DoeBenchmarkRun> RunBenchmarkAsync(byte[] pdf, DateTime nowUtc,
        bool useLocal, CancellationToken cancellationToken,
        Func<string, DoeBenchmarkRun, CancellationToken, Task>? progress)
    {
        _extractionCalls.Clear();
        _benchmarkAiRequests = 0;
        ExtractionUsage = null;
        ExtractionCost = null;
        ExtractionModel = null;
        ExtractionProgress = null;
        var clock = Stopwatch.StartNew();
        var localPages = new List<DoeLocalPageResult>();
        DoeBulkExtraction? extraction = null;
        string? error = null;
        DoeBenchmarkRun Snapshot(string status, string? failure, DoeBulkExtraction? output)
        {
            var allUsageKnown = _benchmarkAiRequests == _extractionCalls.Count;
            return new(status, failure, clock.ElapsedMilliseconds,
                ExtractionModel ?? configuration["OPENAI_MODEL"]?.Trim() ?? "gpt-5.6-luna",
                allUsageKnown ? ExtractionUsage : null, allUsageKnown ? ExtractionCost : null,
                _benchmarkAiRequests, output, ExtractionProgress, localPages.ToArray());
        }
        Task PublishAsync(CancellationToken token) => progress is null ? Task.CompletedTask :
            progress(useLocal ? "hybrid" : "ai", Snapshot("running", null, null), token);
        await PublishAsync(cancellationToken);
        try
        {
            if (useLocal)
            {
                _benchmarkLocalPages = [];
                using var document = new DoePdfPages(pdf);
                DoePageExtraction? context = null;
                for (var number = 1; number <= document.Count; number++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var localClock = Stopwatch.StartNew();
                    var local = DoeLocalTableExtractor.TryExtract(document.ReadPage(number), context, out var reason);
                    localPages.Add(new(number, local is null ? "ai_fallback" : "local", reason,
                        local?.Rows.Count ?? 0, localClock.ElapsedMilliseconds));
                    if (local is not null)
                    {
                        _benchmarkLocalPages[number] = local;
                        context = local;
                    }
                    else context = null; // Never borrow baseline AI output for the hybrid run.
                    await PublishAsync(cancellationToken);
                }
            }
            // No DbContext: neither persisted page caches nor production writes are possible.
            var raw = await ExtractAllAsync(pdf, null, nowUtc,
                progress is null ? null : (_, token) => PublishAsync(token), cancellationToken);
            extraction = raw with { Rows = ValidateBulkRows(raw.Rows).Rows };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (progress is not null)
                await progress(useLocal ? "hybrid" : "ai",
                    Snapshot("interrupted", "Benchmark execution interrupted.", null), CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { error = "Extraction upstream timed out."; }
        catch (HttpRequestException) { error = "Extraction upstream unavailable."; }
        catch (System.Text.Json.JsonException) { error = "Extraction returned invalid JSON."; }
        catch (InvalidDataException ex) { error = ex.Message; }
        finally
        {
            if (_benchmarkLocalPages is not null)
                for (var index = 0; index < localPages.Count; index++)
                    if (localPages[index].Method == "local" && !_benchmarkLocalPages.ContainsKey(localPages[index].PageNumber))
                        localPages[index] = localPages[index] with
                        { Method = "ai_fallback", FallbackReason = "Local output failed shared page validation.", Rows = 0 };
            _benchmarkLocalPages = null;
        }
        var status = error is not null ? "failed" : ExtractionProgress?.RowsSkipped > 0 ? "partial" : "completed";
        var result = Snapshot(status, error, extraction);
        if (progress is not null)
            await progress(useLocal ? "hybrid" : "ai", result, cancellationToken);
        return result;
    }

    public static DoeBenchmarkComparison? CompareBenchmark(DoeBenchmarkRun ai, DoeBenchmarkRun hybrid)
    {
        if (ai.Extraction is null || hybrid.Extraction is null) return null;
        static string Key(ExtractedDoeBulkPrice row) => string.Join('|',
            Normalize(row.City), Normalize(row.Province), Normalize(row.Region ?? ""),
            Normalize(row.OilCompany), Normalize(row.FuelGrade));
        var left = ai.Extraction.Rows.ToDictionary(Key);
        var right = hybrid.Extraction.Rows.ToDictionary(Key);
        var differences = new List<DoeBenchmarkDifference>();
        var matches = 0;
        foreach (var key in left.Keys.Union(right.Keys).Order(StringComparer.Ordinal))
        {
            left.TryGetValue(key, out var a);
            right.TryGetValue(key, out var b);
            if (a is not null && b is not null && a.MinPricePerLiter == b.MinPricePerLiter &&
                a.MaxPricePerLiter == b.MaxPricePerLiter) matches++;
            else differences.Add(new(key, a, b));
        }
        // Local-only has zero API spend. Missing usage on an AI run means unknown, not zero.
        decimal? Cost(DoeBenchmarkRun run) => run.AiCalls == 0 && run.Status == "completed"
            ? 0 : run.EstimatedCost?.TotalCost;
        return new(matches, ai.Extraction.WeekStart == hybrid.Extraction.WeekStart &&
            ai.Extraction.WeekEnd == hybrid.Extraction.WeekEnd, differences, Cost(ai) - Cost(hybrid));
    }
}
