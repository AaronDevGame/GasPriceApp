using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

var grades = new[] { "RON 100", "RON 97", "RON 95", "RON 91", "DIESEL", "DIESEL PLUS", "KEROSENE" };
var companies = new[] { "PETRON", "SHELL", "CALTEX", "PHOENIX", "TOTAL", "FLYING V", "UNIOIL", "SEAOIL", "PTT" };
var expected = companies.SelectMany(company => grades.Select(grade =>
    new ExtractedDoeBulkPrice("Quezon City", "Metro Manila", "National Capital Region", company, grade, 80, 90)))
    .Where(row => !(row.OilCompany == "PTT" && row.FuelGrade == "KEROSENE")).ToArray();
var pdf = MakePdf();
var local = DoeLocalTableExtractor.TryExtract(pdf, null, out var reason);
Check(local?.Rows.Count == 62, $"Expected 62 cells, got {local?.Rows.Count}: {reason}");
Check(local!.Rows.All(row => row.MinPricePerLiter == 80 && row.MaxPricePerLiter == 90), "Price associations.");
Check(DoeLocalTableExtractor.TryExtract(MakePdf(true), null, out _) is null, "Malformed cell must require AI.");
Check(DoeLocalTableExtractor.TryExtract(MakePdf(unsupported: true), null, out _) is null, "Unsupported page must require AI.");
Console.WriteLine("PASS: local column/city/grade associations, blank cells, malformed and unsupported fallback");

var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{ ["OPENAI_API_KEY"] = "test-only", ["OPENAI_MODEL"] = "gpt-6-luna", ["ADMIN_API_KEY"] = "test-admin" }).Build();
var source = new SourceHandler(pdf);
var ai = new AiHandler(expected);
DoeFuelPriceImporter Importer() => new(new HttpClient(source), new ClientFactory(ai), config,
    new TestEnvironment(), NullLogger<DoeFuelPriceImporter>.Instance);
var result = await Importer().BenchmarkAsync("https://doe.gov.ph/test.pdf", DateTime.UtcNow, CancellationToken.None);
Check(result.Ai.AiCalls == 1 && result.Hybrid.AiCalls == 0 && ai.Calls == 1, "Local pages must avoid paid calls.");
Check(result.Comparison?.MatchingRows == 62 && result.Comparison.Differences.Count == 0, "Equal rows must match.");
Check(result.Comparison!.EstimatedApiSavingsUsd > 0, "Local-only run must show zero API spend.");
Check(result.Ai.EstimatedCost?.TotalCost == 0.00015m, "GPT-6 Luna standard rate estimate.");
Check(OpenAiPricing.Estimate("gpt-6-luna", new(300000, 0, 100, 300100), 0)?.TotalCost == 0.060075m,
    "Long-context input/output multiplier.");
Check(source.Calls == 1, "Download exactly once for fair comparison.");
var changed = result.Hybrid with { Extraction = result.Hybrid.Extraction! with
{ Rows = result.Hybrid.Extraction.Rows.Skip(1).Select((row, i) => i == 0 ? row with { MaxPricePerLiter = 91 } : row).ToArray() } };
Check(DoeFuelPriceImporter.CompareBenchmark(result.Ai, changed)?.Differences.Count == 2, "Missing and changed prices.");
Check(DoeFuelPriceImporter.CompareBenchmark(result.Ai with { Extraction = null }, changed) is null, "Failed run has no comparison.");
var fallbackAi = new AiHandler(expected);
var fallback = new DoeFuelPriceImporter(new HttpClient(new SourceHandler(MakePdf(unsupported: true))),
    new ClientFactory(fallbackAi), config, new TestEnvironment(), NullLogger<DoeFuelPriceImporter>.Instance);
var fallbackResult = await fallback.BenchmarkAsync("https://doe.gov.ph/test.pdf", DateTime.UtcNow, CancellationToken.None);
Check(fallbackAi.Calls == 2 && fallbackResult.Hybrid.LocalPages.Single().Method == "ai_fallback", "Independent fallback run.");
var failedAi = new AiHandler(expected, fail: true);
var failed = new DoeFuelPriceImporter(new HttpClient(new SourceHandler(pdf)), new ClientFactory(failedAi),
    config, new TestEnvironment(), NullLogger<DoeFuelPriceImporter>.Instance);
var failedResult = await failed.BenchmarkAsync("https://doe.gov.ph/test.pdf", DateTime.UtcNow, CancellationToken.None);
Check(failedResult.Ai.Status == "failed" && failedResult.Ai.AiCalls == 2 && failedResult.Ai.EstimatedCost is null &&
    failedResult.Comparison is null, "Failed requests must count attempts and preserve unknown cost.");
using (var oversized = new PdfSharp.Pdf.PdfDocument())
{
    for (var i = 0; i < 5; i++) oversized.AddPage();
    using var bytes = new MemoryStream();
    oversized.Save(bytes, closeStream: false);
    var boundedAi = new AiHandler(expected);
    var bounded = new DoeFuelPriceImporter(new HttpClient(new SourceHandler(bytes.ToArray())),
        new ClientFactory(boundedAi), config, new TestEnvironment(), NullLogger<DoeFuelPriceImporter>.Instance);
    try
    {
        await bounded.BenchmarkAsync("https://doe.gov.ph/test.pdf", DateTime.UtcNow, CancellationToken.None);
        throw new Exception("Expected page limit rejection.");
    }
    catch (InvalidDataException) { Check(boundedAi.Calls == 0, "Reject page limit before paid calls."); }
}
Console.WriteLine("PASS: benchmark downloads once, bypasses caches, reports tokens/cost, diffs and AI fallback");

if (args.Length > 0)
{
    using var pages = new DoePdfPages(File.ReadAllBytes(args[0]));
    DoePageExtraction? context = null;
    for (var i = 1; i <= pages.Count; i++)
    {
        var page = DoeLocalTableExtractor.TryExtract(pages.ReadPage(i), context, out var why);
        Console.WriteLine($"Real PDF page {i}: {page?.Rows.Count ?? 0} local rows; fallback: {why}");
        Check(page is not null, "Expected supported NCR fixture to parse locally.");
        context = page;
        if (i == 1)
            Check(page!.Rows.Any(row => row.City == "Caloocan City" && row.OilCompany == "PETRON" && row.FuelGrade == "DIESEL" &&
                row.MinPricePerLiter == 94.30m && row.MaxPricePerLiter == 98.20m), "Manually verified real PDF cell.");
    }
}


Console.WriteLine("Starting HTTP checks");
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
    { Args = [], ContentRootPath = Path.GetFullPath("GasPriceBackend"), EnvironmentName = "Development" });
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Configuration.AddConfiguration(config);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTransient(_ => Importer());
await using var app = builder.Build();
app.UseMiddleware<RateLimitMiddleware>();
app.UseMiddleware<AdminAuthMiddleware>();
app.MapDoeBenchmarkEndpoints("test-instance");
await app.StartAsync();
var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
using var http = new HttpClient { BaseAddress = new Uri(url) };
// Exercise authentication and the shared admin cooldown on the real middleware pipeline.
var unauthorized = await http.PostAsJsonAsync(ApiRoutes.AdminDoeFuelPricesBenchmark, new { sourceUrl = "https://doe.gov.ph/test.pdf" });
Check(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "Admin authentication.");
await Task.Delay(1100);
http.DefaultRequestHeaders.Authorization = new("Bearer", "test-admin");
var invalid = await http.PostAsJsonAsync(ApiRoutes.AdminDoeFuelPricesBenchmark, new { sourceUrl = "https://example.com/test.pdf", unknown = true });
Check(invalid.StatusCode == HttpStatusCode.TooManyRequests, "Benchmark cooldown shares admin bucket.");
await app.StopAsync();
// Validation and success exercise the same endpoint with admin middleware but no cooldown wait.
var directBuilder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
    { Args = [], ContentRootPath = Path.GetFullPath("GasPriceBackend"), EnvironmentName = "Development" });
directBuilder.Logging.ClearProviders();
directBuilder.WebHost.UseUrls("http://127.0.0.1:0");
directBuilder.Configuration.AddConfiguration(config);
directBuilder.Services.AddSingleton(TimeProvider.System);
directBuilder.Services.AddTransient(_ => Importer());
await using var direct = directBuilder.Build();
direct.UseMiddleware<AdminAuthMiddleware>();
direct.MapDoeBenchmarkEndpoints("test-instance");
await direct.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri(direct.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
client.DefaultRequestHeaders.Authorization = new("Bearer", "test-admin");
foreach (var body in new[] { "{}", "[]", "{", "{\"sourceUrl\":7}", "{\"sourceUrl\":\"https://example.com/a.pdf\"}",
    "{\"sourceUrl\":\"https://doe.gov.ph/a.pdf\",\"unknown\":true}",
    "{\"sourceUrl\":\"https://doe.gov.ph/a.pdf\",\"sourceUrl\":\"https://doe.gov.ph/b.pdf\"}" })
{
    var response = await client.PostAsync(ApiRoutes.AdminDoeFuelPricesBenchmark, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
    Check(response.StatusCode == HttpStatusCode.BadRequest, $"Reject invalid request: {body}");
}
var success = await client.PostAsJsonAsync(ApiRoutes.AdminDoeFuelPricesBenchmark, new { sourceUrl = "https://doe.gov.ph/test.pdf" });
Check(success.StatusCode == HttpStatusCode.OK, "HTTP benchmark success without any database service.");
Console.WriteLine("PASS: HTTP admin auth, rate limit, JSON/URL validation and successful response without a database");
await direct.StopAsync();


void Check(bool passed, string message) { if (!passed) throw new Exception(message); }
byte[] MakePdf(bool bad = false, bool unsupported = false)
{
    var builder = new PdfDocumentBuilder();
    var font = builder.AddStandard14Font(Standard14Font.Helvetica);
    var page = builder.AddPage(1100, 800);
    if (unsupported) { page.AddText("Unsupported scanned or regional layout", 9, new PdfPoint(30, 700), font); return builder.Build(); }
    page.AddText("NCR", 9, new PdfPoint(450, 750), font);
    page.AddText("For the week of September 29 - October 5, 2026", 9, new PdfPoint(350, 730), font);
    page.AddText("AREA", 9, new PdfPoint(30, 700), font);
    page.AddText("PRODUCT", 9, new PdfPoint(150, 700), font);
    for (var i = 0; i < companies.Length; i++) page.AddText(companies[i], 9, new PdfPoint(250 + i * 80, 700), font);
    page.AddText("INDEPENDENT", 9, new PdfPoint(970, 700), font);
    page.AddText("Quezon City", 9, new PdfPoint(30, 650), font);
    for (var row = 0; row < grades.Length; row++)
    {
        page.AddText(grades[row], 9, new PdfPoint(150, 680 - row * 10), font);
        for (var column = 0; column < companies.Length; column++)
        {
            if (column == 8 && row == 6) continue;
            page.AddText(bad && column == 2 && row == 3 ? "bad 90.00" : "80.00 90.00", 9,
                new PdfPoint(250 + column * 80, 680 - row * 10), font);
        }
    }
    return builder.Build();
}

sealed class SourceHandler(byte[] pdf) : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pdf) }); }
}
sealed class AiHandler(IReadOnlyList<ExtractedDoeBulkPrice> rows, bool fail = false) : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Calls++;
        if (fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var extraction = new DoePageExtraction("2026-09-29", "2026-10-05", "read", rows);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
        {
            model = "gpt-6-luna", status = "completed",
            usage = new { input_tokens = 1000, output_tokens = 100, total_tokens = 1100 },
            output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(extraction) } } } }
        }) });
    }
}
sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler) { BaseAddress = new Uri("https://api.openai.com/") };
}
sealed class TestEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "BenchmarkChecks";
    public string ContentRootPath { get; set; } = Path.GetFullPath("GasPriceBackend");
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
