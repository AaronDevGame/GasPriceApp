using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class CodexAuthChecks
{
    public static async Task RunAsync()
    {
        foreach (var configuredKey in new string?[] { "codex-fixture", null, "", " " })
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            { Args = ["--hostBuilder:reloadConfigOnChange=false"], ContentRootPath = Path.GetFullPath("GasPriceBackend") });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["ADMIN_API_KEY"] = "admin-fixture";
            builder.Configuration["NEWS_API_KEY"] = "news-fixture";
            builder.Configuration["CODEX_ADMIN_API_KEY"] = configuredKey;
            await using var app = builder.Build();
            app.UseMiddleware<AdminAuthMiddleware>();
            app.MapPost(ApiRoutes.AdminDoeFuelPricesImportExtracted, () => "ok");
            app.MapPost(ApiRoutes.AdminFuelPriceAdjustmentsImportExtracted, () => "ok");
            app.MapGet(ApiRoutes.AdminDoeExtractedReports, () => "ok");
            app.MapGet(ApiRoutes.AdminDoeExtractedJob, (string jobId) => "ok");
            app.MapGet(ApiRoutes.AdminRoutes, () => "ok");
            app.MapGet(ApiRoutes.AdminDoeFuelPricesImportJob, (string jobId) => "ok");
            app.MapPost(ApiRoutes.AdminFuelPriceAdjustmentsImport, () => "ok");
            app.MapGet(ApiRoutes.AdminDoeFuelPricesImportExtracted + "-other", () => "ok");
            app.MapPost(ApiRoutes.AdminFuelNewsImport, () => "ok");
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            foreach (var (method, path) in new[] {
                (HttpMethod.Post, ApiRoutes.AdminDoeFuelPricesImportExtracted),
                (HttpMethod.Post, ApiRoutes.AdminFuelPriceAdjustmentsImportExtracted),
                (HttpMethod.Post, ApiRoutes.AdminFuelPriceAdjustmentsImportExtracted.ToUpperInvariant() + "/"),
                (HttpMethod.Get, ApiRoutes.AdminDoeExtractedReports),
                (HttpMethod.Get, ApiRoutes.AdminDoeExtractedJob.Replace("{jobId}", Guid.Empty.ToString())),
                (HttpMethod.Get, ApiRoutes.AdminDoeExtractedReports.ToUpperInvariant() + "/") })
            {
                await Check(method, path, "codex-fixture", configuredKey == "codex-fixture");
                foreach (var key in new string?[] { "admin-fixture", "news-fixture", "wrong", null, "" })
                    await Check(method, path, key, false);
                await Check(method, path, "codex-fixture", false, "Basic");
            }
            foreach (var path in new[] { ApiRoutes.AdminRoutes,
                ApiRoutes.AdminDoeFuelPricesImportJob.Replace("{jobId}", Guid.Empty.ToString()),
                ApiRoutes.AdminDoeFuelPricesImportExtracted + "-other" })
            {
                await Check(HttpMethod.Get, path, "codex-fixture", false);
                await Check(HttpMethod.Get, path, "admin-fixture", true);
            }
            await Check(HttpMethod.Post, ApiRoutes.AdminFuelNewsImport, "codex-fixture", false);
            await Check(HttpMethod.Post, ApiRoutes.AdminFuelPriceAdjustmentsImport, "codex-fixture", false);
            await Check(HttpMethod.Post, ApiRoutes.AdminFuelPriceAdjustmentsImport, "admin-fixture", true);
            await app.StopAsync();

            async Task Check(HttpMethod method, string path, string? key, bool allowed, string scheme = "Bearer")
            {
                using var message = new HttpRequestMessage(method, path);
                if (key is not null) message.Headers.Authorization = new AuthenticationHeaderValue(scheme, key);
                using var response = await client.SendAsync(message);
                if (response.StatusCode != (allowed ? HttpStatusCode.OK : HttpStatusCode.Unauthorized))
                    throw new Exception($"Unexpected Codex authorization outcome for {method} {path}.");
            }
        }
        Console.WriteLine("PASS: dedicated DOE key isolation, missing/blank config, case/trailing slash and malformed auth");
    }
}
