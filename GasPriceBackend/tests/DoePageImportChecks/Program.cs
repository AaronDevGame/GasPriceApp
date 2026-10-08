using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

// No real AI calls or production database access. Run from the repository root:
// dotnet run --project GasPriceBackend/tests/DoePageImportChecks -- [optional DOE PDF path]
var contentRoot = Path.GetFullPath("GasPriceBackend");
var pdf = args.Length > 0 ? File.ReadAllBytes(args[0]) : MakePdf(4);
using var counted = new DoePdfPages(pdf);
var pageCount = counted.Count;
Check(pageCount >= 4, "The fixture needs at least four pages.");
for (var number = 1; number <= pageCount; number++)
{
    using var stream = new MemoryStream(counted.ReadPage(number));
    using var single = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
    Check(single.PageCount == 1, "Every extracted PDF must contain exactly one page.");
    Check(single.Pages[0].Width.Point == GetPageWidth(pdf, number), "Page order/size must be preserved.");
}
Console.WriteLine($"PASS: split all {pageCount} pages into ordered single-page PDFs");

var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["OPENAI_API_KEY"] = "test-only", ["OPENAI_MODEL"] = "gpt-5.6-luna" }).Build();
var source = new DoeReportSource("mindanao-pump-prices", null,
    new DateOnly(2026, 9, 29), "https://doe.gov.ph/report.pdf");
var now = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
var method = typeof(DoeFuelPriceImporter).GetMethod("ExtractAllAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

async Task<(DoeFuelPriceImporter Importer, FakeExtractor Handler, DoeBulkExtraction? Result,
    List<DoeReportPageProgress> Progress)> RunAsync(string scenario, bool expectFailure = false)
{
    var handler = new FakeExtractor(scenario);
    var importer = CreateImporter(handler);
    var snapshots = new List<DoeReportPageProgress>();
    Func<DoeReportPageProgress, CancellationToken, Task> progress = (snapshot, _) =>
    {
        snapshots.Add(snapshot);
        return Task.CompletedTask;
    };
    var task = (Task<DoeBulkExtraction>)method.Invoke(importer,
        [pdf, source, now, progress, CancellationToken.None])!;
    DoeBulkExtraction? result = null;
    try
    {
        result = await task;
        Check(!expectFailure, "Expected a failed report.");
    }
    catch (InvalidDataException) when (expectFailure) { }
    Check(handler.Pages.All(number => number >= 1 && number <= pageCount), "Unexpected page number.");
    Check(handler.Pages.Distinct().Count() == pageCount, "No later page may be skipped.");
    var billedCalls = handler.Pages.Count - (scenario == "retry" ? 1 : 0);
    Check(importer.ExtractionUsage?.InputTokens == billedCalls * 100,
        "Billing must include every page and billed retry.");
    Check(importer.ExtractionUsage?.OutputTokens == billedCalls * 10, "Output tokens must be summed.");
    Check(importer.ExtractionCost is not null, "The supported model must have summed cost.");
    Check(snapshots[0].PagesTotal == pageCount && snapshots[0].PagesCompleted == 0,
        "The page count must be visible before the first request.");
    Check(snapshots.Any(snapshot => snapshot.CurrentPage == 4 && snapshot.PagesCompleted == 3),
        "Live progress must reach page 4 after the independent-only page.");
    return (importer, handler, result, snapshots);
}

var success = await RunAsync("success");
Check(success.Importer.ExtractionProgress!.PagesCompleted == pageCount, "All pages must complete.");
Check(success.Importer.ExtractionProgress.Pages[2].PriceRows == 0, "Independent-only page has no company rows.");
Check(DoeFuelPriceImporter.ValidateBulkRows(success.Result!.Rows).Rows.Count == pageCount - 1,
    "Independent prices must not be retained.");
var reportStatus = new DoeImportReportStatus("mindanao-pump-prices", null, source.WeekStart,
    source.Url, "running", 0, 0, null) { PageProgress = success.Progress[3] };
var status = DoeImportWorker.ToStatus(new DoeImportJob
    { DetailsJson = JsonSerializer.Serialize(new[] { reportStatus }) });
var responseJson = JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));
Check(responseJson.Contains("\"pageProgress\"") && responseJson.Contains("\"pagesTotal\""),
    "Polling must expose persisted page progress with API JSON names.");
Check(DoeImportWorker.ToStatus(new DoeImportJob()).Reports.Count == 0, "Legacy jobs must remain readable.");
Console.WriteLine("PASS: independent-only page completes; later pages run; polling JSON retains progress and billing");

var retry = await RunAsync("retry");
Check(retry.Handler.Pages.Count(number => number == 4) == 2 && retry.Handler.Pages.Count == pageCount + 1,
    "Retry only the failed page, never earlier successful pages.");
Check(retry.Importer.ExtractionProgress!.PagesFailed == 0, "A recovered retry must not count as failed.");
Console.WriteLine("PASS: retry only the failed page and include retry billing");

foreach (var scenario in new[] { "failed", "date_conflict", "incomplete", "bad_price" })
{
    var failed = await RunAsync(scenario, expectFailure: true);
    Check(failed.Importer.ExtractionProgress!.PagesFailed == 1, "The failed page must be visible.");
    Check(failed.Importer.ExtractionProgress.PagesCompleted == pageCount - 1,
        "A failed page must not count as completed.");
    Console.WriteLine($"PASS: {scenario} prevents completion while remaining pages are processed");
}

var canceledHandler = new FakeExtractor("success");
var canceledImporter = CreateImporter(canceledHandler);
using var cancellation = new CancellationTokenSource();
Func<DoeReportPageProgress, CancellationToken, Task> cancelProgress = (snapshot, _) =>
{
    if (snapshot.CurrentPage == 4) cancellation.Cancel();
    return Task.CompletedTask;
};
try
{
    await (Task<DoeBulkExtraction>)method.Invoke(canceledImporter,
        [pdf, source, now, cancelProgress, cancellation.Token])!;
    throw new Exception("Cancellation must interrupt the import.");
}
catch (OperationCanceledException) { }
Check(canceledHandler.Pages.Count <= 3, "Cancellation must not retry or start another page request.");
Console.WriteLine("PASS: cancellation interrupts without retrying");

// The public import method must reject page failures before opening a DB transaction.
var failedImporter = CreateImporter(new FakeExtractor("failed"));
using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
    .UseNpgsql("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1").Options);
try
{
    await failedImporter.ImportReportFromUrlAsync(db, source.Url, now, CancellationToken.None);
    throw new Exception("A failed report must not be saved.");
}
catch (InvalidDataException error)
{
    Check(error.Message.Contains("failed page"), "Page failure must reject the report before DB access.");
}
Console.WriteLine("PASS: failed public import never accesses the database");

if (int.TryParse(Environment.GetEnvironmentVariable("DOE_TEST_PGPORT"), out var testPort))
    await DoePageJobIntegration.RunAsync(contentRoot, pdf, pageCount, testPort);

DoeFuelPriceImporter CreateImporter(FakeExtractor handler) => new(
    new HttpClient(new PdfHandler(pdf)), new ClientFactory(handler), config,
    new TestEnvironment(contentRoot), NullLogger<DoeFuelPriceImporter>.Instance);

static byte[] MakePdf(int count)
{
    using var document = new PdfDocument();
    for (var index = 0; index < count; index++)
        document.AddPage().Width = PdfSharp.Drawing.XUnit.FromPoint(400 + index);
    using var buffer = new MemoryStream();
    document.Save(buffer, closeStream: false);
    return buffer.ToArray();
}

static double GetPageWidth(byte[] pdf, int number)
{
    using var stream = new MemoryStream(pdf);
    using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
    return document.Pages[number - 1].Width.Point;
}

static void Check(bool condition, string error)
{
    if (!condition) throw new Exception(error);
}

sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler)
        { BaseAddress = new Uri("https://api.openai.com/") };
}

sealed class PdfHandler(byte[] pdf) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var content = request.RequestUri!.AbsolutePath.EndsWith(".pdf")
            ? (HttpContent)new ByteArrayContent(pdf)
            : new StringContent(request.RequestUri.AbsolutePath.EndsWith("mindanao-pump-prices")
                ? "<h2>2026</h2><a href=\"https://doe.gov.ph/report.pdf\">September 29 to October 5</a>"
                : "<h2>2026</h2>");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}

sealed class FakeExtractor(string scenario) : HttpMessageHandler
{
    public List<int> Pages { get; } = [];
    public TaskCompletionSource PageFourStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleasePageFour { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var content = body.RootElement.GetProperty("input")[0].GetProperty("content");
        var prompt = content[0].GetProperty("text").GetString()!;
        var number = int.Parse(System.Text.RegularExpressions.Regex.Match(prompt, @"original page (\d+)").Groups[1].Value);
        var data = content[1].GetProperty("file_data").GetString()!.Split(',', 2)[1];
        using var stream = new MemoryStream(Convert.FromBase64String(data));
        using var single = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        if (single.PageCount != 1) throw new Exception("The AI received more than one page.");
        Pages.Add(number);
        if (scenario == "gated" && number == 4)
        {
            PageFourStarted.TrySetResult();
            await ReleasePageFour.Task.WaitAsync(token);
        }
        if (scenario == "retry" && number == 4 && Pages.Count(page => page == 4) == 1)
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var bad = number == 4;
        var extraction = new DoePageExtraction(
            scenario == "date_conflict" && bad ? "2026-09-22" : "2026-09-29",
            scenario == "date_conflict" && bad ? "2026-09-28" : "2026-10-05",
            scenario == "failed" && bad ? "unreadable" : "read",
            [new ExtractedDoeBulkPrice("City " + number, "Basilan", "Region IX",
                number == 3 ? "INDEPENDENT" : "PETRON", "RON 91",
                scenario == "bad_price" && bad ? 500 : 90, 90)]);
        var result = new
        {
            model = "gpt-5.6-luna", status = scenario == "incomplete" && bad ? "incomplete" : "completed",
            usage = new { input_tokens = 100, output_tokens = 10, total_tokens = 110,
                input_tokens_details = new { cached_tokens = 0 } },
            output = new[] { new { type = "message", content = new[]
                { new { type = "output_text", text = JsonSerializer.Serialize(extraction) } } } }
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") };
    }
}

sealed class TestEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "DoePageImportChecks";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
