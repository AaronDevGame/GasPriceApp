using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class NewsAuthChecks
{
    public static async Task RunAsync()
    {
        const string adminKey = "isolated-admin-fixture";
        const string newsKey = "isolated-news-fixture";
        Console.WriteLine("Starting isolated news authorization HTTP checks");
        foreach (var configuredNewsKey in new string?[] { newsKey, null, "", " " })
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = ["--hostBuilder:reloadConfigOnChange=false"],
                ContentRootPath = Path.GetFullPath("GasPriceBackend")
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["ADMIN_API_KEY"] = adminKey;
            builder.Configuration["NEWS_API_KEY"] = configuredNewsKey;
            builder.Configuration["INSTANCE_ID"] = "news-auth-checks";
            await using var app = builder.Build();
            app.UseMiddleware<AdminAuthMiddleware>();
            app.MapPost(ApiRoutes.AdminFuelNewsImport, () => "accepted");
            app.MapGet(ApiRoutes.AdminFuelNewsRevisions, (string id) => "accepted");
            app.MapGet(ApiRoutes.AdminRoutes, () => "accepted");
            app.MapGet(ApiRoutes.FuelNews, () => "public");
            await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                var address = app.Services.GetRequiredService<IServer>().Features
                    .Get<IServerAddressesFeature>()!.Addresses.Single();
                using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
                var hasNewsKey = configuredNewsKey == newsKey;
                foreach (var (method, path) in new[] {
                    (HttpMethod.Post, ApiRoutes.AdminFuelNewsImport),
                    (HttpMethod.Get, ApiRoutes.AdminFuelNewsRevisions.Replace("{id}", Guid.Empty.ToString())) })
                {
                    await Check(method, path, newsKey, hasNewsKey ? HttpStatusCode.OK : HttpStatusCode.Unauthorized,
                        hasNewsKey ? null : AuthErrors.InvalidNewsKey);
                    await Check(method, path, adminKey, HttpStatusCode.Unauthorized, AuthErrors.InvalidNewsKey);
                    await Check(method, path, null, HttpStatusCode.Unauthorized, AuthErrors.MissingAuthorizationHeader);
                    await Check(method, path, "wrong-key", HttpStatusCode.Unauthorized, AuthErrors.InvalidNewsKey);
                    await Check(method, path, "", HttpStatusCode.Unauthorized, AuthErrors.InvalidNewsKey);
                    await Check(method, path, newsKey, HttpStatusCode.Unauthorized, AuthErrors.InvalidNewsKey, "Basic");
                }
                await Check(HttpMethod.Get, ApiRoutes.AdminRoutes, adminKey, HttpStatusCode.OK);
                await Check(HttpMethod.Get, ApiRoutes.AdminRoutes, newsKey, HttpStatusCode.Unauthorized, AuthErrors.InvalidAdminKey);
                await Check(HttpMethod.Get, ApiRoutes.FuelNews, null, HttpStatusCode.OK);
                // A similarly named sibling must not inherit the news credential scope.
                await Check(HttpMethod.Get, ApiRoutes.AdminFuelNews + "-other", newsKey,
                    HttpStatusCode.Unauthorized, AuthErrors.InvalidAdminKey);

                async Task Check(HttpMethod method, string path, string? key, HttpStatusCode expected,
                    string? expectedError = null, string scheme = "Bearer")
                {
                    using var request = new HttpRequestMessage(method, path);
                    if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue(scheme, key);
                    using var response = await http.SendAsync(request);
                    if (response.StatusCode != expected)
                        throw new Exception($"News auth: {method} {path} returned {response.StatusCode}, expected {expected}.");
                    if (expectedError is not null)
                    {
                        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                        if (body.RootElement.GetProperty("code").GetInt32() != 401 ||
                            body.RootElement.GetProperty("error").GetProperty("error").GetString() != expectedError)
                            throw new Exception("News auth: unexpected unauthorized envelope.");
                    }
                }
            }
            finally { await app.StopAsync(); }
        }
        Console.WriteLine("PASS HTTP news/admin key isolation, revision protection, missing/blank configuration, malformed credentials and public reads");
    }
}
