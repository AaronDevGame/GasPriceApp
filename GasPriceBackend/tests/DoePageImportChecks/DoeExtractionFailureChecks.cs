using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

static class DoeExtractionFailureChecks
{
    public static async Task RunAsync(byte[] pdf, DoeReportSource source, DateTime now, string root)
    {
        foreach (var scenario in new[] { "bad_request", "unauthorized", "quota", "quota_type", "unsafe_identifiers", "malformed_error",
            "long_retry_after", "rate_limit", "overload", "network", "timeout", "cancel_delay" })
        {
            using var handler = new FailureHandler(scenario);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["OPENAI_API_KEY"] = "test-only", ["OPENAI_MODEL"] = "gpt-5.6-luna" }).Build();
            var importer = new DoeFuelPriceImporter(new HttpClient(new PdfHandler(pdf)), new ClientFactory(handler),
                config, new TestEnvironment(root), NullLogger<DoeFuelPriceImporter>.Instance);
            var method = typeof(DoeFuelPriceImporter).GetMethod("ExtractAllAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var snapshots = new List<DoeReportPageProgress>();
            using var cancellation = new CancellationTokenSource();
            Func<DoeReportPageProgress, CancellationToken, Task> progress = (snapshot, _) =>
            {
                snapshots.Add(snapshot);
                if (scenario == "cancel_delay" && snapshot.Pages[3].Status == "retrying")
                    cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
                return Task.CompletedTask;
            };
            var transient = scenario is "rate_limit" or "overload" or "network" or "timeout";
            try
            {
                await (Task<DoeBulkExtraction>)method.Invoke(importer,
                    [pdf, source, now, progress, cancellation.Token, null, null])!;
                Check(transient, "Permanent failures must prevent report completion.");
            }
            catch (InvalidDataException) when (!transient && scenario != "cancel_delay") { }
            catch (OperationCanceledException) when (scenario == "cancel_delay") { }
            if (transient)
            {
                Check(handler.Attempts.Count == 2 && handler.Attempts[1] - handler.Attempts[0] >=
                    TimeSpan.FromSeconds(scenario == "rate_limit" ? 2 : 1),
                    "Transient failures must wait before retrying.");
                Check(importer.ExtractionProgress!.PagesFailed == 0, "Recovered errors must not fail the report.");
            }
            else
            {
                Check(handler.Attempts.Count == 1, "Permanent failures, long server delays, and cancellation must not retry.");
                var failure = snapshots.Last(snapshot => snapshot.Pages[3].Error is not null).Pages[3];
                Check(failure.HttpStatus == (scenario is "unauthorized" ? 401 : scenario is "bad_request" or "unsafe_identifiers" or "malformed_error" ? 400 : 429),
                    "Page progress must retain HTTP status.");
                if (scenario == "unsafe_identifiers")
                    Check(failure.RequestId is null && failure.ErrorCode is null, "Unsafe identifiers must be discarded.");
                else
                    Check(failure.RequestId == "req_doe_test" && failure.Error!.Contains("req_doe_test"),
                        "Sanitized provider request IDs must survive in failure details.");
                var serialized = JsonSerializer.Serialize(failure);
                Check(!serialized.Contains("SECRET_PROVIDER_MESSAGE") && !serialized.Contains("sk-secret"),
                    "Provider messages and unsafe identifier values must never enter persisted diagnostics.");
                Check(JsonSerializer.Deserialize<DoeImportPageStatus>(serialized)!.HttpStatus == failure.HttpStatus,
                    "HTTP diagnostics must survive persisted progress serialization.");
                if (scenario is "quota" or "quota_type")
                    Check(failure.ErrorCode == (scenario == "quota" ? "insufficient_quota" : "unknown_quota"),
                        "Quota codes must be preserved while quota failures do not retry.");
            }
            Console.WriteLine($"PASS: extraction {scenario} diagnostics and retry handling");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class FailureHandler(string scenario) : DelegatingHandler(new FakeExtractor("success"))
    {
        public List<DateTimeOffset> Attempts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var prompt = body.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
            if (!prompt.Contains("original page 4 ")) return await base.SendAsync(request, token);
            Attempts.Add(DateTimeOffset.UtcNow);
            if (Attempts.Count > 1) return await base.SendAsync(request, token);
            if (scenario == "network") throw new HttpRequestException("SECRET_PROVIDER_MESSAGE");
            if (scenario == "timeout") throw new TaskCanceledException("SECRET_PROVIDER_MESSAGE");
            var status = scenario is "bad_request" or "unsafe_identifiers" or "malformed_error" ? HttpStatusCode.BadRequest :
                scenario == "unauthorized" ? HttpStatusCode.Unauthorized :
                scenario == "overload" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.TooManyRequests;
            var code = scenario == "quota" ? "insufficient_quota" : scenario == "quota_type" ? "unknown_quota" :
                scenario == "unsafe_identifiers" ? "sk-secret\nunsafe" : "rate_limit_exceeded";
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { error = new
                {
                    code, type = scenario == "quota_type" ? "insufficient_quota" : "api_error",
                    message = "SECRET_PROVIDER_MESSAGE sk-secret"
                } }))
            };
            if (scenario == "malformed_error") response.Content = new StringContent("SECRET_PROVIDER_MESSAGE not JSON");
            response.Headers.TryAddWithoutValidation("x-request-id", scenario == "unsafe_identifiers" ? "sk-secret" : "req_doe_test");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(
                scenario == "long_retry_after" ? 120 : scenario == "cancel_delay" ? 30 : scenario == "rate_limit" ? 2 : 1));
            return response;
        }
    }
}
