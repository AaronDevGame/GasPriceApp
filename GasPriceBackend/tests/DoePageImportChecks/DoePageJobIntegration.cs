using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

static class DoePageJobIntegration
{
    // Optional checks require a disposable localhost PostgreSQL cluster; never accept a connection URL.
    public static async Task RunAsync(string root, byte[] pdf, int pageCount, int port)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var connection = $"Host=127.0.0.1;Port={port};Database=gasprice_doe_page_test;Username={Environment.UserName}";
        var extractor = new FakeExtractor("gated");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            { Args = [], ContentRootPath = root, EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ADMIN_API_KEY"] = "test-only-admin-key",
            ["OPENAI_API_KEY"] = "test-only", ["OPENAI_MODEL"] = "gpt-5.6-luna"
        });
        builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connection));
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<DoeImportWorker>();
        builder.Services.AddSingleton<IHttpClientFactory>(new ClientFactory(extractor));
        builder.Services.AddTransient(provider => new DoeFuelPriceImporter(
            new HttpClient(new PdfHandler(pdf)), provider.GetRequiredService<IHttpClientFactory>(),
            builder.Configuration, builder.Environment,
            provider.GetRequiredService<ILogger<DoeFuelPriceImporter>>()));
        builder.Services.AddTransient(provider => new GeoapifyReverseGeocodingClient(
            new HttpClient(), builder.Configuration));
        await using var app = builder.Build();
        app.UseMiddleware<RateLimitMiddleware>();
        app.UseMiddleware<AdminAuthMiddleware>();
        app.MapDoeImportEndpoints("test-instance");
        app.MapDoeFuelPriceEndpoints(new AuthService(), "test-instance");
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var pollPath = ApiRoutes.AdminDoeFuelPricesImportJob.Replace("{jobId}", Guid.NewGuid().ToString());
        Check((await client.GetAsync(pollPath)).StatusCode == HttpStatusCode.Unauthorized,
            "Anonymous job polling must be rejected.");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-only-admin-key");
        await Task.Delay(1100); // Keep the actual admin cooldown.
        using var created = await client.PostAsJsonAsync(ApiRoutes.AdminDoeFuelPricesImportJobs,
            new { mode = "backfill", from = "2026-09-29", to = "2026-10-05" });
        Check(created.StatusCode == HttpStatusCode.Accepted, "The job endpoint must accept the backfill.");
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var jobId = createdJson.RootElement.GetProperty("data").GetProperty("jobId").GetGuid();
        pollPath = ApiRoutes.AdminDoeFuelPricesImportJob.Replace("{jobId}", jobId.ToString());
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.DoeImportJobs.SingleAsync(item => item.Id == jobId);
            job.Status = "running";
            job.StartedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        var worker = app.Services.GetRequiredService<DoeImportWorker>();
        var run = (Task)typeof(DoeImportWorker).GetMethod("RunJobAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(worker, [jobId, CancellationToken.None])!;
        try
        {
            await extractor.PageFourStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(1100);
            using var live = await client.GetAsync(pollPath);
            Check(live.IsSuccessStatusCode, "Live polling must succeed.");
            using var liveJson = JsonDocument.Parse(await live.Content.ReadAsStringAsync());
            var report = liveJson.RootElement.GetProperty("data").GetProperty("reports")[0];
            var progress = report.GetProperty("pageProgress");
            Check(report.GetProperty("status").GetString() == "running" &&
                progress.GetProperty("pagesTotal").GetInt32() == pageCount &&
                progress.GetProperty("pagesCompleted").GetInt32() == 3 &&
                progress.GetProperty("currentPage").GetInt32() == 4,
                "The GET endpoint must expose persisted 3/N progress while page 4 is waiting.");
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Check(await db.DoeFuelPrices.CountAsync() == 0, "No page prices may be saved before all pages finish.");
            Console.WriteLine("PASS: authenticated JobID polling exposes live page 4 progress before any prices are saved");
        }
        finally
        {
            extractor.ReleasePageFour.TrySetResult();
            await run;
        }
        await Task.Delay(1100);
        using var finished = await client.GetAsync(pollPath);
        using var finishedJson = JsonDocument.Parse(await finished.Content.ReadAsStringAsync());
        var data = finishedJson.RootElement.GetProperty("data");
        Check(data.GetProperty("status").GetString() == "completed", "The completed job must be pollable.");
        Check(data.GetProperty("reports")[0].GetProperty("pageProgress").GetProperty("pagesCompleted").GetInt32() == pageCount,
            "Final progress must count all pages.");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Check(await db.DoeFuelPrices.CountAsync() == pageCount - 1,
                "Only named company prices, including later pages, should be stored.");
            Check(!await db.DoeFuelPrices.AnyAsync(price => price.OilCompany == "INDEPENDENT"),
                "Independent prices must not enter the database.");
        }
        await Task.Delay(1100);
        using var direct = await client.PostAsJsonAsync(ApiRoutes.AdminDoeFuelPricesImport,
            new { sourceUrl = "https://doe.gov.ph/report.pdf" });
        Check(direct.IsSuccessStatusCode, "The synchronous report endpoint must still succeed.");
        using var directJson = JsonDocument.Parse(await direct.Content.ReadAsStringAsync());
        var directData = directJson.RootElement.GetProperty("data");
        Check(directData.GetProperty("status").GetString() == "already_imported" &&
            directData.GetProperty("added").GetInt32() == 0 &&
            directData.GetProperty("pageProgress").GetProperty("pagesCompleted").GetInt32() == pageCount,
            "An unchanged synchronous import must return final page totals and no duplicate inserts.");
        Console.WriteLine("PASS: job completes with named-company rows; synchronous repeat returns final totals without duplicates");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stale = new DoeImportJob
            {
                Id = Guid.NewGuid(), Status = "running", ActiveSlot = 1,
                CreatedAtUtc = DateTime.UtcNow.AddHours(-1), HeartbeatAtUtc = DateTime.UtcNow.AddHours(-1),
                DetailsJson = JsonSerializer.Serialize(new[]
                {
                    new DoeImportReportStatus("mindanao-pump-prices", null, new DateOnly(2026, 9, 29),
                        "https://doe.gov.ph/report.pdf", "running", 0, 0, null)
                    { PageProgress = new DoeReportPageProgress(pageCount, 3, 4, []) }
                })
            };
            db.DoeImportJobs.Add(stale);
            await db.SaveChangesAsync();
            await (Task)typeof(DoeImportWorker).GetMethod("RecoverStaleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(worker, [CancellationToken.None])!;
            db.ChangeTracker.Clear();
            var recovered = DoeImportWorker.ToStatus(await db.DoeImportJobs.SingleAsync(job => job.Id == stale.Id));
            Check(recovered.Status == "interrupted" && recovered.Reports[0].Status == "interrupted" &&
                recovered.Reports[0].PageProgress?.PagesCompleted == 3,
                "Crash recovery must retain page progress without leaving the report marked running.");
        }
        Console.WriteLine("PASS: interrupted jobs preserve page progress and mark the running report interrupted");
        await app.StopAsync();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
