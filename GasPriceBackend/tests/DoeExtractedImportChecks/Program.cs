using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Offline HTTP/auth/queue checks. No external HTTP clients are registered.
// Optional real persistence checks use only a disposable localhost database:
// DOE_TEST_PGPORT=<port> dotnet run --project GasPriceBackend/tests/DoeExtractedImportChecks
ProvincePriceChecks.Run();
await CodexAuthChecks.RunAsync();
var clock = new TestClock();
var captured = new List<DoeImportJob>();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    { Args = [], EnvironmentName = "Production", ContentRootPath = Path.GetFullPath("GasPriceBackend") });
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    { ["ADMIN_API_KEY"] = "test-admin", ["NEWS_API_KEY"] = "test-news", ["CODEX_ADMIN_API_KEY"] = "test-codex", ["OPENAI_API_KEY"] = null });
builder.Services.AddSingleton<TimeProvider>(clock);
builder.Services.AddSingleton<DoeImportWorker>();
var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(
    "Host=127.0.0.1;Port=1;Database=never_connect;Username=test").Options;
builder.Services.AddScoped<AppDbContext>(_ => new CapturingDb(options, captured));
await using var app = builder.Build();
app.UseMiddleware<RateLimitMiddleware>();
app.UseMiddleware<AdminAuthMiddleware>();
app.MapDoeExtractedImportEndpoints("test-instance");
await app.StartAsync();
Console.WriteLine("Started isolated HTTP validation host.");
using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
    .Features.Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromSeconds(10) };
const string valid = """
{"sourceUrl":"https://doe.gov.ph/test.pdf","weekStart":"2026-10-27","weekEnd":"2026-11-02",
 "complete":true,"rows":[{"city":"Quezon City","province":"Metro Manila","region":"National Capital Region",
 "oilCompany":"PETRON","fuelGrade":"DIESEL","minPricePerLiter":60,"maxPricePerLiter":65}]}
""";
async Task<HttpResponseMessage> Post(string body, string? key = "test-codex", string type = "application/json", bool advance = true)
{
    if (advance) clock.Advance();
    using var message = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.AdminDoeFuelPricesImportExtracted)
        { Content = new StringContent(body, Encoding.UTF8, type) };
    // Oversized bodies can be rejected before upload completes; isolate each test's connection.
    message.Headers.ConnectionClose = true;
    message.Headers.ExpectContinue = true;
    if (key is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    return await client.SendAsync(message);
}
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
foreach (var path in new[] {
    ApiRoutes.AdminDoeExtractedReports,
    ApiRoutes.AdminDoeExtractedReports + "?sourceUrl=https://example.com/test.pdf",
    ApiRoutes.AdminDoeExtractedReports + "?sourceUrl=https://doe.gov.ph/test.pdf&extra=1",
    ApiRoutes.AdminDoeExtractedReports + "?sourceUrl=https://doe.gov.ph/test.pdf&sourceUrl=https://doe.gov.ph/test.pdf",
    ApiRoutes.AdminDoeExtractedJob.Replace("{jobId}", "invalid") })
{
    clock.Advance();
    using var message = new HttpRequestMessage(HttpMethod.Get, path);
    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-codex");
    using var response = await client.SendAsync(message);
    Check(response.StatusCode == HttpStatusCode.BadRequest, "Invalid read parameters must fail before database access.");
}
Console.WriteLine("PASS: report lookup and job polling reject invalid parameters before database access");
foreach (var key in new string?[] { null, "wrong", "test-news", "test-admin" })
{
    using var response = await Post(valid, key);
    Check(response.StatusCode == HttpStatusCode.Unauthorized, "Only the dedicated Codex credential may submit prices.");
}
using (var response = await Post(valid, type: "text/plain"))
    Check(response.StatusCode == HttpStatusCode.BadRequest, "Reject non-JSON bodies.");
foreach (var invalid in new[]
{
    "null", "[]", "{", valid.Replace("\"complete\":true,", ""),
    valid.Replace("\"complete\":true", "\"complete\":null"),
    valid.Replace("\"complete\":true", "\"complete\":true,\"complete\":false"),
    valid.Replace("\"city\":", "\"extra\":true,\"city\":"),
    valid.Replace("\"province\":\"Metro Manila\"", "\"province\":null"),
    valid.Replace("\"province\":\"Metro Manila\"", "\"province\":\"\""),
    valid.Replace("\"city\":\"Quezon City\"", "\"city\":42"),
    valid.Replace("\"city\":\"Quezon City\"", "\"city\":\"Quezon City\",\"city\":\"Manila\""),
    valid.Replace("\"minPricePerLiter\":60", "\"minPricePerLiter\":\"60\""),
    valid.Replace("\"minPricePerLiter\":60", "\"minPricePerLiter\":0"),
    valid.Replace("\"minPricePerLiter\":60", "\"minPricePerLiter\":66"),
    valid.Replace("\"minPricePerLiter\":60", "\"minPricePerLiter\":60.123"),
    valid.Replace("\"DIESEL\"", "\"LPG\""), valid.Replace("\"PETRON\"", "\"OVERALL\""),
    valid.Replace("https://doe.gov.ph/test.pdf", "https://example.com/test.pdf"),
    valid.Replace("test.pdf", "retail-pump-prices"),
    valid.Replace("2026-11-02", "2026-11-20"), valid.Replace("2026-10-27", "2099-10-27"),
    valid.Replace("\"complete\":true", "\"coverageSource\":\"invented\",\"complete\":true"),
    valid.Replace("\"oilCompany\":", "\"monitoringStart\":\"2026-10-28\",\"oilCompany\":"),
    valid.Replace("\"oilCompany\":", "\"monitoringStart\":\"2026-10-29\",\"monitoringEnd\":\"2026-10-28\",\"oilCompany\":"),
    new string(' ', 2 * 1024 * 1024 + 1)
})
{
    using var response = await Post(invalid);
    Check(response.StatusCode == HttpStatusCode.BadRequest,
        $"Invalid submission returned {response.StatusCode}: {invalid[..Math.Min(100, invalid.Length)]}");
}
Check(captured.Count == 0, "Invalid or unauthorized data must not create any jobs.");
Console.WriteLine("PASS: HTTP authentication, JSON/body bounds, source, dates, companies and prices reject before writes");
using (var response = await Post(valid))
{
    Check(response.StatusCode == HttpStatusCode.Accepted, "Valid external data must queue without an OpenAI key/client.");
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var job = captured.Single();
    var status = json.RootElement.GetProperty("data");
    Check(job.Mode == "extracted" && status.GetProperty("mode").GetString() == "extracted" &&
        status.GetProperty("status").GetString() == "queued" && response.Headers.Location?.ToString() ==
        ApiRoutes.AdminDoeExtractedJob.Replace("{jobId}", job.Id.ToString()), "Return the Codex-scoped polling route.");
    var payload = JsonSerializer.Deserialize<DoeExtractedImportRequest>(job.RequestJson!)!;
    Check(payload.Rows.Count == 1 && payload.Complete &&
        DoeImportWorker.ToStatus(job).Request?.SourceUrl == payload.SourceUrl, "Persist rows but return only request summary.");
}
using (var response = await Post(valid, advance: false))
    Check(response.StatusCode == HttpStatusCode.TooManyRequests && response.Headers.RetryAfter is not null,
        "The extracted endpoint must retain the shared admin cooldown.");
Console.WriteLine("PASS: valid external submission returns 202 and durable payload; admin rate limit retained");

var duplicate = JsonSerializer.Deserialize<DoeExtractedImportRequest>(captured.Single().RequestJson!)!;
try
{
    DoeFuelPriceImporter.ValidateSubmittedReport(duplicate with { Rows = [duplicate.Rows[0],
        duplicate.Rows[0] with { MinPricePerLiter = 61 }] }, clock.GetUtcNow().UtcDateTime);
    throw new Exception("Conflicting duplicate prices must fail.");
}
catch (InvalidDataException) { }
try
{
    DoeFuelPriceImporter.ValidateSubmittedReport(duplicate with { Rows = [duplicate.Rows[0],
        duplicate.Rows[0] with { MonitoringStart = new(2026, 10, 28), MonitoringEnd = new(2026, 10, 29) }] },
        clock.GetUtcNow().UtcDateTime);
    throw new Exception("Conflicting monitoring dates must fail.");
}
catch (InvalidDataException) { }
Console.WriteLine("PASS: duplicate price and monitoring conflicts reject atomically");
foreach (var cityValue in new[] { "null", "\"\"", "\"  \"", "omit" })
{
    var payload = valid.Replace("\"province\":\"Metro Manila\"", "\"province\":\"Marinduque\"")
        .Replace("\"region\":\"National Capital Region\"", "\"region\":\"Mimaropa\"");
    payload = cityValue == "omit" ? payload.Replace("\"city\":\"Quezon City\",", "")
        : payload.Replace("\"city\":\"Quezon City\"", "\"city\":" + cityValue);
    using var response = await Post(payload);
    Check(response.StatusCode == HttpStatusCode.Accepted, "A province-level row with optional city must queue.");
    var submitted = JsonSerializer.Deserialize<DoeExtractedImportRequest>(captured.Last().RequestJson!)!;
    var normalized = DoeFuelPriceImporter.ValidateSubmittedReport(submitted, clock.GetUtcNow().UtcDateTime);
    Check(normalized.Rows.Single().City == "" && normalized.Rows.Single().Province == "Marinduque",
        "Omitted, null and blank cities must have identical provincial storage keys.");
}
Console.WriteLine("PASS: HTTP accepts omitted/null/blank city with a required province");


if (int.TryParse(Environment.GetEnvironmentVariable("DOE_TEST_PGPORT"), out var port))
{
    if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
    var pgOptions = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(
        $"Host=127.0.0.1;Port={port};Database=gasprice_doe_submitted_test;Username={Environment.UserName}").Options;
    await using var db = new AppDbContext(pgOptions);
    await db.Database.MigrateAsync();
    await db.DoeFuelPrices.ExecuteDeleteAsync();
    await db.DoePumpPriceReports.ExecuteDeleteAsync();
    await db.DoeImportJobs.ExecuteDeleteAsync();
    await db.FuelPriceCaches.ExecuteDeleteAsync();
    db.FuelPriceCaches.Add(new FuelPriceCache { Scope = FuelPriceCacheScopes.Province,
        Province = "Metro Manila", ProvinceKey = "METRO MANILA", CachedAt = clock.GetUtcNow().UtcDateTime,
        RefreshAfter = clock.GetUtcNow().UtcDateTime.AddDays(1) });
    await db.SaveChangesAsync();
    // Recreate a worker scope with the real database, but with no network clients or API key.
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<TimeProvider>(clock);
    services.AddSingleton<IConfiguration>(builder.Configuration);
    services.AddScoped(_ => new AppDbContext(pgOptions));
    services.AddSingleton<DoeImportWorker>();
    services.AddScoped(provider => new DoeFuelPriceImporter(new HttpClient(new RejectNetwork()),
        new RejectClients(), builder.Configuration, builder.Environment,
        provider.GetRequiredService<ILogger<DoeFuelPriceImporter>>()));
    await using var provider = services.BuildServiceProvider();
    var worker = provider.GetRequiredService<DoeImportWorker>();
    async Task<DoeImportJobStatus> Run(DoeExtractedImportRequest payload)
    {
        var job = new DoeImportJob { Id = Guid.NewGuid(), Mode = "extracted",
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime, RequestJson = JsonSerializer.Serialize(payload) };
        db.DoeImportJobs.Add(job);
        await db.SaveChangesAsync();
        await (Task)typeof(DoeImportWorker).GetMethod("ProcessQueuedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(worker, [CancellationToken.None])!;
        db.ChangeTracker.Clear();
        return DoeImportWorker.ToStatus(await db.DoeImportJobs.SingleAsync(row => row.Id == job.Id));
    }
    var first = await Run(duplicate);
    Check(first.Status == "completed" && first.PriceRowsAdded == 1, "Worker must save submitted data.");
    var saved = await db.DoeFuelPrices.AsNoTracking().SingleAsync();
    var cached = await db.FuelPriceCaches.AsNoTracking().SingleAsync();
    Check(saved.MinPricePerLiter == 60 && cached.RefreshAfter == clock.GetUtcNow().UtcDateTime,
        "Persist prices and expire affected research caches together.");
    var repeat = await Run(duplicate);
    Check(repeat.Status == "completed" && repeat.ReportsSkipped == 1 && repeat.PriceRowsAdded == 0 &&
        repeat.PriceRowsUpdated == 0 && (await db.DoeFuelPrices.AsNoTracking().SingleAsync()).FetchedAtUtc == saved.FetchedAtUtc,
        "Retries must neither duplicate prices nor change unchanged timestamps.");
    var changed = await Run(duplicate with { Rows = [duplicate.Rows[0] with { MaxPricePerLiter = 66 }] });
    Check(changed.PriceRowsUpdated == 1 && changed.PriceRowsAdded == 0, "Corrections update the existing price.");
    var partial = await Run(duplicate with { Complete = false });
    Check(partial.Status == "completed_with_gaps" && (await db.DoePumpPriceReports.AsNoTracking().SingleAsync()).Status == "partial",
        "Partial submissions must not certify report completeness.");
    Check(await db.DoeFuelPrices.CountAsync() == 1 && await db.DoePageCaches.CountAsync() == 0,
        "No duplicate prices or fabricated PDF caches.");
    Console.WriteLine("PASS: PostgreSQL queue execution, upserts, corrections, partial reports and cache invalidation without network");
    var provincial = duplicate with
    {
        SourceUrl = "https://doe.gov.ph/mimaropa.pdf",
        Rows = [duplicate.Rows[0] with { City = null, Province = "Marinduque", Region = "Mimaropa" }]
    };
    var taskNow = clock.GetUtcNow().UtcDateTime;
    db.FuelPriceCaches.AddRange(new[] { "Boac", "Gasan" }.Select(city => new FuelPriceCache
    {
        Scope = FuelPriceCacheScopes.City, City = city, CityKey = city.ToUpperInvariant(),
        Province = "Marinduque", ProvinceKey = "MARINDUQUE", CachedAt = taskNow,
        RefreshAfter = taskNow.AddDays(1)
    }));
    await db.SaveChangesAsync();
    var provincialJob = await Run(provincial);
    Check(provincialJob.Status == "completed" && provincialJob.PriceRowsAdded == 1 &&
        (await db.DoePumpPriceReports.SingleAsync(r => r.SourceUrl == provincial.SourceUrl)).Status == "imported",
        "Province-only company prices must save and complete the report.");
    var provincialRow = await db.DoeFuelPrices.AsNoTracking().SingleAsync(p => p.ProvinceKey == "MARINDUQUE");
    Check(provincialRow.City == "" && provincialRow.CityKey == "" &&
        await db.FuelPriceCaches.CountAsync(c => c.ProvinceKey == "MARINDUQUE" && c.RefreshAfter == taskNow) == 2,
        "Persist an empty city key and expire all municipality caches within the province.");
    var repeatedProvince = await Run(provincial with { Rows = [provincial.Rows[0] with { City = "  " }] });
    Check(repeatedProvince.PriceRowsAdded == 0 && repeatedProvince.PriceRowsUpdated == 0 &&
        await db.DoeFuelPrices.CountAsync(p => p.ProvinceKey == "MARINDUQUE") == 1,
        "Null/blank city reimports must not duplicate province-level rows.");
    using var scope = provider.CreateScope();
    var importer = scope.ServiceProvider.GetRequiredService<DoeFuelPriceImporter>();
    var boac = new ResolvedFuelLocation("Boac", "Marinduque", "Mimaropa");
    var read = await importer.GetAsync(db, boac, taskNow, CancellationToken.None);
    Check(read.Count == 1 && DoeLocationMatcher.IsProvincePrice(read[0]),
        "Boac users must receive the stored Marinduque range without network extraction.");
    var oldBoac = new DoeFuelPrice
    {
        City = "Boac", CityKey = "BOAC", Province = "Marinduque", ProvinceKey = "MARINDUQUE",
        Region = "Mimaropa", WeekStart = new(2026, 10, 26), WeekEnd = new(2026, 11, 1),
        OilCompany = "PETRON", FuelGrade = "DIESEL", MinPricePerLiter = 59, MaxPricePerLiter = 60,
        SourceUrl = "https://doe.gov.ph/older.pdf", FetchedAtUtc = taskNow
    };
    db.DoeFuelPrices.Add(oldBoac);
    await db.SaveChangesAsync();
    read = await importer.ReadAsync(db, boac, taskNow, CancellationToken.None);
    Check(read.Count == 1 && DoeLocationMatcher.IsProvincePrice(read[0]),
        "Older still-fresh Boac coverage must not hide a newer Marinduque range.");
    var specific = await Run(provincial with { Rows = [provincial.Rows[0] with { City = "Boac" }] });
    read = await importer.ReadAsync(db, boac, taskNow, CancellationToken.None);
    Check(specific.PriceRowsAdded == 1 && read.Count == 1 && read[0].City == "Boac" &&
        await db.DoeFuelPrices.CountAsync(p => p.ProvinceKey == "MARINDUQUE" && p.CityKey == "") == 1,
        "City data wins for the same week without overwriting the provincial record.");
    read = await importer.ReadAsync(db, boac, taskNow.AddDays(20), CancellationToken.None);
    Check(read.Count == 0, "Neither city nor province fallback may return expired reports.");
    Console.WriteLine("PASS: PostgreSQL provincial completeness, repeat imports, cache expiry, Boac fallback and fresh city preference");

}
else Console.WriteLine("SKIP: PostgreSQL persistence checks (set DOE_TEST_PGPORT for a disposable local cluster)");
await app.StopAsync();

sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance() => _now = _now.AddSeconds(2);
}
sealed class CapturingDb(DbContextOptions<AppDbContext> options, List<DoeImportJob> jobs) : AppDbContext(options)
{
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        jobs.AddRange(ChangeTracker.Entries<DoeImportJob>().Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity));
        return Task.FromResult(1);
    }
}
sealed class RejectNetwork : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new Exception("External imports must never download or call AI.");
}
sealed class RejectClients : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new RejectNetwork());
}
