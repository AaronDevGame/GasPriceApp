using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

static class DoeBenchmarkJobChecks
{
    // Only accept an isolated localhost test cluster, never a production connection string.
    public static async Task RunAsync(byte[] pdf, IReadOnlyList<ExtractedDoeBulkPrice> rows, int port)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var source = new SourceHandler(pdf);
        var ai = new GatedAiHandler(rows);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        { Args = [], ContentRootPath = Path.GetFullPath("GasPriceBackend"), EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["ADMIN_API_KEY"] = "test-admin";
        builder.Configuration["OPENAI_API_KEY"] = "test-only";
        builder.Configuration["OPENAI_MODEL"] = "gpt-6-luna";
        builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(
            $"Host=127.0.0.1;Port={port};Database=gasprice_benchmark_test;Username={Environment.UserName}"));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<DoeImportWorker>();
        builder.Services.AddTransient(provider => new DoeFuelPriceImporter(new HttpClient(source),
            new ClientFactory(ai), builder.Configuration, new TestEnvironment(),
            provider.GetRequiredService<ILogger<DoeFuelPriceImporter>>()));
        await using var app = builder.Build();
        app.UseMiddleware<AdminAuthMiddleware>();
        app.MapDoeBenchmarkEndpoints("test-instance");
        Guid unrelatedId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
            var unrelated = new DoeImportJob { Id = Guid.NewGuid(), Mode = "latest", Status = "completed", CreatedAtUtc = DateTime.UtcNow };
            unrelatedId = unrelated.Id;
            db.DoeImportJobs.Add(unrelated);
            await db.SaveChangesAsync();
        }
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
        var anonymous = await http.PostAsJsonAsync(ApiRoutes.AdminDoeFuelPricesBenchmark, new { sourceUrl = "https://doe.gov.ph/test.pdf" });
        Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "Anonymous benchmark must not queue.");
        http.DefaultRequestHeaders.Authorization = new("Bearer", "test-admin");
        var hidden = await http.GetAsync(ApiRoutes.AdminDoeFuelPricesBenchmarkJob.Replace("{jobId}", unrelatedId.ToString()));
        Check(hidden.StatusCode == HttpStatusCode.NotFound, "Benchmark poll must exclude unrelated imports.");
        var missing = await http.GetAsync(ApiRoutes.AdminDoeFuelPricesBenchmarkJob.Replace("{jobId}", Guid.NewGuid().ToString()));
        Check(missing.StatusCode == HttpStatusCode.NotFound, "Unknown UUID returns 404.");
        using var created = await http.PostAsJsonAsync(ApiRoutes.AdminDoeFuelPricesBenchmark,
            new { sourceUrl = "https://doe.gov.ph/test.pdf" });
        Check(created.StatusCode == HttpStatusCode.Accepted && source.Calls == 0 && ai.Calls == 0,
            "POST returns 202 before downloading or extracting.");
        using var accepted = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var data = accepted.RootElement.GetProperty("data");
        var id = data.GetProperty("jobId").GetGuid();
        var path = data.GetProperty("statusUrl").GetString()!;
        Check(path == ApiRoutes.AdminDoeFuelPricesBenchmarkJob.Replace("{jobId}", id.ToString()) &&
            created.Headers.Location?.ToString() == path && data.GetProperty("result").ValueKind == JsonValueKind.Null,
            "Accepted response carries polling Location and no premature result.");
        var worker = app.Services.GetRequiredService<DoeImportWorker>();
        Task Process() => (Task)typeof(DoeImportWorker).GetMethod("ProcessQueuedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(worker, [CancellationToken.None])!;
        var run = Process();
        try
        {
            await ai.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var live = await http.GetAsync(path);
            Check(live.Headers.CacheControl?.NoStore == true, "Live job responses must not be cached.");
            using var liveJson = JsonDocument.Parse(await live.Content.ReadAsStringAsync());
            var liveData = liveJson.RootElement.GetProperty("data");
            Check(liveData.GetProperty("status").GetString() == "running" &&
                liveData.GetProperty("result").ValueKind == JsonValueKind.Null &&
                liveData.GetProperty("reports")[0].GetProperty("section").GetString() == "ai" &&
                liveData.GetProperty("reports")[0].GetProperty("pageProgress").GetProperty("currentPage").GetInt32() == 1 &&
                liveData.GetProperty("reports")[1].GetProperty("status").GetString() == "queued",
                "Live polling exposes the running AI page and queued hybrid phase.");
            await Process(); // A second worker pass cannot acquire the active database slot.
            Check(ai.Calls == 1, "Active slot prevents duplicate benchmark execution.");
        }
        finally { ai.Release.TrySetResult(); await run; }
        // The request which enqueued work is long gone; read the durable result in a new request.
        using var finished = await http.GetAsync(path);
        using var finishedJson = JsonDocument.Parse(await finished.Content.ReadAsStringAsync());
        var final = finishedJson.RootElement.GetProperty("data");
        Check(final.GetProperty("status").GetString() == "completed" &&
            final.GetProperty("usage").GetProperty("inputTokens").GetInt32() == 1000 &&
            final.GetProperty("result").GetProperty("comparison").GetProperty("matchingRows").GetInt32() == rows.Count &&
            final.GetProperty("result").GetProperty("hybrid").GetProperty("aiCalls").GetInt32() == 0,
            "Polling returns the persisted full comparison after completion.");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.DoeImportJobs.AsNoTracking().SingleAsync(job => job.Id == id);
            Check(stored.ActiveSlot is null && stored.FinishedAtUtc is not null &&
                stored.PriceRowsAdded == 0 && stored.PriceRowsUpdated == 0 && stored.ResultJson is not null &&
                await db.DoeFuelPrices.CountAsync() == 0 && await db.DoePumpPriceReports.CountAsync() == 0 &&
                await db.DoePageCaches.CountAsync() == 0,
                "Persist only diagnostic job state; price/report/cache tables remain unchanged.");
        }
        // Another queued benchmark is picked up from the database, not from a request-bound task.
        using var second = await http.PostAsJsonAsync(ApiRoutes.AdminDoeFuelPricesBenchmark,
            new { sourceUrl = "https://doe.gov.ph/test.pdf" });
        Check(second.StatusCode == HttpStatusCode.Accepted, "A subsequent benchmark may queue.");
        await Process();
        Check(ai.Calls == 2 && source.Calls == 2, "Repeated benchmarks bypass extraction caches.");
        ai.BlockNext();
        using var third = await http.PostAsJsonAsync(ApiRoutes.AdminDoeFuelPricesBenchmark,
            new { sourceUrl = "https://doe.gov.ph/test.pdf" });
        using var thirdJson = JsonDocument.Parse(await third.Content.ReadAsStringAsync());
        var thirdId = thirdJson.RootElement.GetProperty("data").GetProperty("jobId").GetGuid();
        using var stopping = new CancellationTokenSource();
        var interrupted = (Task)typeof(DoeImportWorker).GetMethod("ProcessQueuedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(worker, [stopping.Token])!;
        await ai.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await stopping.CancelAsync();
        await interrupted;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stopped = await db.DoeImportJobs.AsNoTracking().SingleAsync(job => job.Id == thirdId);
            var stoppedStatus = DoeImportWorker.ToStatus(stopped);
            Check(stopped.Status == "interrupted" && stopped.ActiveSlot is null && stopped.ResultJson is null &&
                stoppedStatus.Reports[0].Status == "interrupted" && stoppedStatus.Reports[1].Status == "not_run" &&
                stoppedStatus.EstimatedCost is null,
                "Interrupted work must release the active slot, preserve unknown billing, and not report completion.");
        }
        await app.StopAsync();
        Console.WriteLine("PASS: durable benchmark 202/polling, live phases, serialization, persisted results, interruption and no price/cache writes");
    }

    private static void Check(bool passed, string message) { if (!passed) throw new Exception(message); }
    private sealed class GatedAiHandler(IReadOnlyList<ExtractedDoeBulkPrice> rows) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blockNext;
        public int Calls;
        public void BlockNext()
        {
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _blockNext = 1;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Interlocked.Increment(ref Calls) == 1 || Interlocked.Exchange(ref _blockNext, 0) == 1)
            { Started.TrySetResult(); await Release.Task.WaitAsync(token); }
            using var invoker = new HttpMessageInvoker(new AiHandler(rows));
            return await invoker.SendAsync(request, token);
        }
    }
}
