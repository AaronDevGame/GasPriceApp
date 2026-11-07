using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

internal static class McpChecks
{
    private const string Issuer = "https://issuer.example.test/";
    private const string Resource = "https://backend.example.test/mcp";

    public static async Task RunAsync()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "mcp-test-only" };
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        foreach (var enabled in new[] { false, true })
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = ["--hostBuilder:reloadConfigOnChange=false"],
                ContentRootPath = Path.GetFullPath("GasPriceBackend")
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mcp:Enabled"] = enabled.ToString(), ["Mcp:Authority"] = Issuer,
                ["Mcp:Resource"] = Resource, ["Mcp:AllowedSubjects"] = "owner",
                ["Mcp:PublishEnabled"] = "false", ["ADMIN_API_KEY"] = "admin-fixture",
                ["NEWS_API_KEY"] = "news-fixture"
            });
            builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(
                "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1"));
            var clock = new TestClock(DateTime.UtcNow);
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddScoped<FuelNewsService>();
            builder.Services.AddFuelNewsMcp(builder.Configuration);
            // In-memory issuer configuration: no live OAuth account, production key or database.
            builder.Services.PostConfigure<JwtBearerOptions>(FuelNewsMcp.Scheme, options =>
            {
                options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                options.Configuration.SigningKeys.Add(key);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(options.Configuration);
            });
            await using var app = builder.Build();
            app.UseMiddleware<RateLimitMiddleware>();
            app.UseMiddleware<AdminAuthMiddleware>();
            app.UseAuthentication();
            app.UseFuelNewsMcpBoundary("mcp-checks");
            app.UseAuthorization();
            app.MapFuelNewsMcp("mcp-checks");
            await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) };
                var publisher = Token("owner", FuelNewsMcp.ReadScope + " " + FuelNewsMcp.PublishScope);
                if (!enabled)
                {
                    using var disabled = await Send("{}", publisher);
                    Check(disabled.StatusCode == HttpStatusCode.ServiceUnavailable, "Disabled MCP must fail closed.");
                    continue;
                }
                using (var metadataResponse = await http.GetAsync(ApiRoutes.McpMetadata))
                {
                    var metadata = await metadataResponse.Content.ReadAsStringAsync();
                    Check(metadataResponse.IsSuccessStatusCode && metadata.Contains(Resource) && metadata.Contains(Issuer), "OAuth resource discovery failed.");
                    Check(!metadata.Contains("news-fixture") && !metadata.Contains("admin-fixture"), "Discovery exposed a key.");
                }
                // Discovery may be requested concurrently by multiple MCP startup clients.
                await Task.WhenAll(Enumerable.Range(1, 59).Select(async i =>
                {
                    using var response = await http.GetAsync(i % 2 == 0
                        ? ApiRoutes.McpMetadata.ToUpperInvariant() + "/" : ApiRoutes.McpMetadata);
                    Check(response.StatusCode == HttpStatusCode.OK, "OAuth discovery startup burst was blocked.");
                }));
                await ExpectDiscoveryLimit(60);
                using (var independent = await Send("{}", null))
                    Check(independent.StatusCode == HttpStatusCode.Unauthorized,
                        "A full discovery bucket blocked the independent MCP route or bypassed authentication.");
                clock.Now = clock.Now.AddSeconds(59);
                await ExpectDiscoveryLimit(1);
                clock.Now = clock.Now.AddSeconds(1);
                using (var recovered = await http.GetAsync(ApiRoutes.McpMetadata))
                    Check(recovered.StatusCode == HttpStatusCode.OK, "OAuth discovery did not recover after Retry-After.");
                foreach (var invalid in new string?[] { null, "news-fixture", "admin-fixture", "guest-fixture",
                    Token("owner", FuelNewsMcp.ReadScope, audience: "wrong-resource"),
                    Token("owner", FuelNewsMcp.ReadScope, issuer: "https://wrong-issuer.test/"),
                    Token("owner", FuelNewsMcp.ReadScope, expired: true),
                    Token("owner", FuelNewsMcp.ReadScope, signing: new SigningCredentials(new RsaSecurityKey(RSA.Create(2048)), SecurityAlgorithms.RsaSha256)) })
                {
                    using var denied = await Send("{}", invalid);
                    Check(denied.StatusCode == HttpStatusCode.Unauthorized, "Missing/invalid/expired/wrong-audience/wrong-issuer/signature token was accepted.");
                    Check(denied.Headers.WwwAuthenticate.ToString().Contains("oauth-protected-resource/mcp"), "401 must advertise resource metadata.");
                    var error = await denied.Content.ReadAsStringAsync();
                    Check(!error.Contains("fixture"), "Authentication response exposed credentials.");
                }
                foreach (var deniedToken in new[] { Token("stranger", FuelNewsMcp.ReadScope), Token("owner", "unrelated"), Token("owner", FuelNewsMcp.PublishScope) })
                {
                    using var denied = await Send("{}", deniedToken);
                    Check(denied.StatusCode == HttpStatusCode.Forbidden, $"Expected 403 for unlisted subject/missing scope, received {(int)denied.StatusCode}.");
                }
                var init = await Rpc("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "isolated-checks", version = "1" } });
                Check(init.TryGetProperty("result", out var initialized) && initialized.TryGetProperty("serverInfo", out _), "MCP initialize failed.");
                var tools = await Rpc("tools/list", new { });
                var definitions = tools.GetProperty("result").GetProperty("tools");
                Check(definitions.GetArrayLength() == 3, "Expected exactly three fuel-news tools.");
                var publishSchema = definitions.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "publish_or_update");
                Check(publishSchema.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean(), "Publish must be annotated as destructive.");
                var schema = publishSchema.GetProperty("inputSchema").GetRawText();
                Check(schema.Contains("article") && schema.Contains("expectedRevision") && schema.Contains("publishedAtUtc"), "Missing article/revision schema.");
                await ExpectToolError("list_news", new { limit = 0 }, "limit");
                await ExpectToolError("list_news", new { cursor = "invalid" }, "cursor");
                await ExpectToolError("read_article", new { id = "invalid" }, "UUID");
                var article = new FuelNewsContent("mcp-fixture", "mcp-fixture", "general", "general",
                    "Isolated fixture article", "An isolated fixture summary that is never published.",
                    "This fixture verifies disabled publishing and is never saved to any database.", null, null,
                    DateTime.UtcNow.AddDays(1), [], [new("Fixture", "https://example.com/fixture", DateTime.UtcNow)]);
                await ExpectToolError("publish_or_update", new { article, expectedRevision = (int?)null }, "disabled");
                var readOnly = await Rpc("tools/call", new { name = "publish_or_update", arguments = new { article, expectedRevision = (int?)null } }, Token("owner", FuelNewsMcp.ReadScope));
                Check(IsError(readOnly), "Read-only OAuth token could publish.");
                // A scoped publisher reaches the existing validation when explicitly enabled.
                builder.Configuration["Mcp:PublishEnabled"] = "true";
                await ExpectToolError("publish_or_update", new { article = article with { Title = "bad" }, expectedRevision = (int?)null }, "title");
                builder.Configuration["Mcp:PublishEnabled"] = "false";
                foreach (var bad in new[] {
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"id\":2,\"method\":\"tools/list\"}",
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"list_news\",\"arguments\":{\"extra\":true}}}",
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"publish_or_update\",\"arguments\":{\"article\":{}}}}",
                    "[{}]", "{broken", "{\"pad\":\"" + new string('x', 32768) + "\"}" })
                {
                    using var rejected = await Send(bad, publisher);
                    Check(rejected.StatusCode == HttpStatusCode.BadRequest, "Invalid/duplicate/oversized/unknown/missing JSON was accepted.");
                }
                using (var wrongOrigin = await Send("{}", publisher, origin: "https://untrusted.example"))
                    Check(wrongOrigin.StatusCode == HttpStatusCode.Forbidden, "Cross-origin request was accepted.");
                using (var nonJson = await Send("{}", publisher, contentType: "text/plain"))
                    Check(nonJson.StatusCode == HttpStatusCode.BadRequest, "Non-JSON content was accepted.");
                // Count auth failures too; case/trailing slash variations share the MCP bucket.
                var limited = false;
                for (var i = 0; i < 61; i++)
                {
                    using var response = await Send("{}", null, path: i % 2 == 0 ? "/MCP/" : ApiRoutes.Mcp);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        Check(response.Headers.RetryAfter is not null, "Rate limit needs Retry-After.");
                        limited = true; break;
                    }
                }
                Check(limited, "MCP rate limit was bypassed.");

                string Token(string sub, string scopes, string audience = Resource, string issuer = Issuer, bool expired = false, SigningCredentials? signing = null)
                    => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                    {
                        Issuer = issuer, Audience = audience, Subject = new ClaimsIdentity([new("sub", sub), new("scope", scopes)]),
                        NotBefore = DateTime.UtcNow.AddMinutes(-10), Expires = DateTime.UtcNow.AddMinutes(expired ? -5 : 5),
                        SigningCredentials = signing ?? credentials
                    });
                async Task<HttpResponseMessage> Send(string body, string? token, string path = ApiRoutes.Mcp, string? origin = null, string contentType = "application/json")
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, path);
                    request.Content = new StringContent(body, Encoding.UTF8, contentType);
                    request.Headers.Accept.ParseAdd("application/json, text/event-stream");
                    request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
                    if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (origin is not null) request.Headers.Add("Origin", origin);
                    return await http.SendAsync(request);
                }
                async Task ExpectDiscoveryLimit(int seconds)
                {
                    using var response = await http.GetAsync(ApiRoutes.McpMetadata);
                    Check(response.StatusCode == HttpStatusCode.TooManyRequests, "OAuth discovery rate limit was bypassed.");
                    Check(response.Headers.RetryAfter?.Delta == TimeSpan.FromSeconds(seconds), "OAuth discovery Retry-After is incorrect.");
                    var error = await response.Content.ReadAsStringAsync();
                    Check(error.Contains("too_many_requests"), "OAuth discovery limit lost the shared API error envelope.");
                }
                async Task<JsonElement> Rpc(string method, object parameters, string? token = null)
                {
                    using var response = await Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), token ?? publisher);
                    var content = await response.Content.ReadAsStringAsync();
                    Check(response.StatusCode == HttpStatusCode.OK, $"MCP {method} returned HTTP {(int)response.StatusCode}.");
                    if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                        content = string.Join("\n", content.Split('\n').Where(line => line.StartsWith("data: ")).Select(line => line[6..]));
                    return JsonDocument.Parse(content).RootElement.Clone();
                }
                async Task ExpectToolError(string name, object arguments, string expected)
                {
                    var result = await Rpc("tools/call", new { name, arguments });
                    Check(IsError(result) && result.GetRawText().Contains(expected, StringComparison.OrdinalIgnoreCase), $"Expected safe {expected} tool error for {name}.");
                }
            }
            finally { await app.StopAsync(); }
        }
        Console.WriteLine("PASS MCP OAuth discovery, JWT validation, subject/scopes, initialization, schemas, disabled publishing, validation, bounded JSON, origin and rate limits");
    }

    private static bool IsError(JsonElement result) => result.TryGetProperty("error", out _) ||
        (result.TryGetProperty("result", out var body) && body.TryGetProperty("isError", out var error) && error.GetBoolean());
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
