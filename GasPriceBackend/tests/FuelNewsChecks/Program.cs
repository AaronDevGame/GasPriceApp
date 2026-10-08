using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;
using Npgsql;

var connection = Environment.GetEnvironmentVariable("FUEL_NEWS_TEST_CONNECTION")
    ?? throw new InvalidOperationException("Run tests/fuel_news_smoke.py to provide an isolated local database.");
var parsed = new NpgsqlConnectionStringBuilder(connection);
if (parsed.Host != "127.0.0.1" || parsed.Database != "gasprice_news_test")
    throw new InvalidOperationException("Checks require the dedicated local test database.");
var clock = new TestClock(DateTime.UtcNow);
var services = new ServiceCollection().AddDbContext<AppDbContext>(o => o.UseNpgsql(connection))
    .AddSingleton<TimeProvider>(clock).BuildServiceProvider();
using var scope = services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
var owner = Guid.NewGuid().ToString();
db.Guests.Add(new Guest { AppInstanceId = owner, PlayerId = 987654321012345, PlayerName = "News test fixture",
    CreatedAt = clock.Now, LastLoginAt = clock.Now, IsLoggedIn = true });
db.FuelNewsSubscriptions.Add(new FuelNewsSubscription { AppInstanceId = owner,
    PushToken = "ExpoPushToken[news-test-fixture-123456]", Enabled = true, IncludeForecasts = false, UpdatedAtUtc = clock.Now });
await db.SaveChangesAsync();
var service = new FuelNewsService(db, clock);
var date = DateOnly.FromDateTime(clock.Now.AddHours(8)).AddDays(2);
var topic = "test-adjustment-" + date.ToString("yyyy-MM-dd");
var forecast = new FuelNewsContent(topic + "-forecast", topic, "adjustment", "forecast",
    "Fixture fuel adjustment forecast", "Fixture summary for isolated database and delivery checks only.",
    "This fixture exercises forecast supersession, revision history, and notifications without publishing real news.",
    date, null, date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-8),
    [new("gasoline", null, 1, 2, "forecast")], [new("Fixture source", "https://example.com/news", clock.Now)]);
var first = await service.ImportAsync(new(forecast, null), default);
Check(!await db.FuelNewsDeliveries.AnyAsync(d => d.NewsId == first.Item.Id), "Confirmed-only subscriber received a forecast.");
var retry = await service.ImportAsync(new(forecast, null), default);
Check(retry.Status == "already_imported" && retry.Item.Revision == 1, "Retry changed the revision.");
var confirmed = forecast with { ImportKey = topic + "-confirmed", Status = "confirmed",
    Title = "Fixture confirmed fuel adjustment", Adjustments = [new("gasoline", null, 2, 2, "confirmed")] };
var saved = await service.ImportAsync(new(confirmed, null), default);
Check(first.Item.Id != saved.Item.Id, "Confirmation overwrote the forecast.");
Check((await db.FuelNews.FindAsync(first.Item.Id))!.SupersededById == saved.Item.Id, "Forecast was not superseded.");
Check(await db.FuelNewsDeliveries.CountAsync(d => d.NewsId == saved.Item.Id) == 1, "Publication/outbox are not consistent.");
await service.ImportAsync(new(confirmed, null), default);
Check(await db.FuelNewsDeliveries.CountAsync(d => d.NewsId == saved.Item.Id) == 1, "Retry duplicated a push.");
var edited = confirmed with { Body = confirmed.Body + " This is a wording correction." };
await service.ImportAsync(new(edited, 1), default);
Check(await db.FuelNewsDeliveries.CountAsync(d => d.NewsId == saved.Item.Id) == 1, "Wording correction caused another push.");
try { await service.ImportAsync(new(edited with { Title = "Concurrent fixture correction" }, 1), default); throw new Exception("Stale update accepted."); }
catch (FuelNewsConflictException) { }
var material = edited with { Adjustments = [new("gasoline", null, 3, 3, "confirmed")] };
await service.ImportAsync(new(material, 2), default);
Check(await db.FuelNewsRevisions.CountAsync(r => r.NewsId == saved.Item.Id) == 3, "Revision history was not retained.");
Check(await db.FuelNewsDeliveries.CountAsync(d => d.NewsId == saved.Item.Id) == 2, "Material correction did not queue a push.");
Console.WriteLine("PASS import retries, revision conflicts, forecast supersession, and material-only outbox publication");

db.FuelAdjustments.Add(new FuelAdjustment { WeekStart = date, WeekEnd = date.AddDays(6),
    EffectiveDatePhilippines = date, OilCompany = "Fixture company", GasolineChangePerLiter = 3,
    SourceUrl = "https://example.com/doe.pdf", FetchedAtUtc = clock.Now });
await db.SaveChangesAsync();
try { await service.ImportAsync(new(material with { Adjustments = [new("gasoline", null, 4, 4, "confirmed")] }, 3), default); throw new Exception("DOE conflict accepted."); }
catch (InvalidDataException) { }
Check(await db.FuelNewsRevisions.CountAsync(r => r.NewsId == saved.Item.Id) == 3, "Rejected import partially saved.");
Console.WriteLine("PASS stored DOE consistency and rejected-import atomicity");

var handler = new FakePush();
var worker = new FuelNewsNotificationWorker(services.GetRequiredService<IServiceScopeFactory>(), new FakeClients(handler),
    new ConfigurationBuilder().Build(), clock, NullLogger<FuelNewsNotificationWorker>.Instance);
await worker.ProcessAsync(default);
db.ChangeTracker.Clear();
var deliveries = await db.FuelNewsDeliveries.Where(d => d.NewsId == saved.Item.Id).OrderBy(d => d.Revision).ToListAsync();
Check(deliveries[0].Status == "cancelled" && deliveries[1].Status == "ticketed", "Obsolete revision was sent or ticket was not saved.");
Check(handler.Sends == 1, "Unexpected send count.");
clock.Now = clock.Now.AddMinutes(16);
await worker.ProcessAsync(default);
db.ChangeTracker.Clear();
Check((await db.FuelNewsDeliveries.FindAsync(deliveries[1].Id))!.Status == "accepted", "Receipt was not saved.");
Check(handler.Sends == 1, "Receipt polling resent a notification.");
Console.WriteLine("PASS obsolete-revision cancellation, persisted tickets, and receipt polling without resending");

// Create a fresh topic to exercise transient send failures and DeviceNotRegistered receipts.
var another = material with { ImportKey = "test-second-confirmed", TopicKey = "test-second-topic",
    ExpiresAtUtc = clock.Now.AddDays(1) };
var other = await new FuelNewsService(db, clock).ImportAsync(new(another, null), default);
handler.FailNext = true;
await worker.ProcessAsync(default);
db.ChangeTracker.Clear();
var failed = await db.FuelNewsDeliveries.SingleAsync(d => d.NewsId == other.Item.Id);
Check(failed.Status == "queued" && failed.Attempts == 1 && failed.Error == "expo_http_503", "Transient failure was not queued with backoff.");
clock.Now = clock.Now.AddMinutes(3);
await worker.ProcessAsync(default);
clock.Now = clock.Now.AddMinutes(16);
handler.InvalidDevice = true;
await worker.ProcessAsync(default);
db.ChangeTracker.Clear();
Check(!(await db.FuelNewsSubscriptions.FindAsync(owner))!.Enabled, "Invalid device was not disabled.");
Check((await db.FuelNewsDeliveries.FindAsync(failed.Id))!.Status == "failed", "Permanent delivery failure was retried.");
Console.WriteLine("PASS transient failure retries and permanent invalid-device removal (all Expo calls mocked)");

var currentSubscription = (await db.FuelNewsSubscriptions.FindAsync(owner))!;
currentSubscription.Enabled = true;
currentSubscription.UpdatedAtUtc = clock.Now;
await db.SaveChangesAsync();
var rotating = material with { ImportKey = "test-rotating-confirmed", TopicKey = "test-rotating-topic",
    ExpiresAtUtc = clock.Now.AddDays(1) };
await new FuelNewsService(db, clock).ImportAsync(new(rotating, null), default);
await worker.ProcessAsync(default);
clock.Now = clock.Now.AddMinutes(1);
currentSubscription.PushToken = "ExpoPushToken[rotated-valid-token-fixture]";
currentSubscription.UpdatedAtUtc = clock.Now;
await db.SaveChangesAsync();
clock.Now = clock.Now.AddMinutes(16);
await worker.ProcessAsync(default);
db.ChangeTracker.Clear();
Check((await db.FuelNewsSubscriptions.FindAsync(owner))!.Enabled, "A receipt for an old token disabled its replacement.");
Console.WriteLine("PASS stale invalid-device receipts preserve newly registered tokens");

// Exercise opt-in/opt-out HTTP routes with sending enabled, without registering a push worker.
var auth = new AuthService();
var access = auth.GenerateSecret();
var guest = (await db.Guests.FindAsync(owner))!;
guest.AccessTokenHash = auth.HashSecret(access);
guest.AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1);
var otherOwner = Guid.NewGuid().ToString();
var otherAccess = auth.GenerateSecret();
db.Guests.Add(new Guest { AppInstanceId = otherOwner, PlayerId = 987654321012346, PlayerName = "News second fixture",
    AccessTokenHash = auth.HashSecret(otherAccess), AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1),
    CreatedAt = clock.Now, LastLoginAt = clock.Now, IsLoggedIn = true });
await db.SaveChangesAsync();
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Configuration["FuelNews:PushEnabled"] = "true";
builder.Services.AddSingleton<TimeProvider>(clock);
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
await using var app = builder.Build();
app.MapFuelNewsSubscriptionEndpoints(auth, "news-tests");
await app.StartAsync();
try
{
    using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    http.DefaultRequestHeaders.Authorization = new("Bearer", access);
    http.DefaultRequestHeaders.Add("X-App-Instance-Id", owner);
    var body = new FuelNewsSubscriptionRequest("ExpoPushToken[http-device-fixture-123]", false);
    using var optIn = await http.PutAsJsonAsync(ApiRoutes.FuelNewsSubscription, body);
    Check(optIn.StatusCode == HttpStatusCode.OK, "Native authenticated opt-in failed.");
    using var preferences = JsonDocument.Parse(await http.GetStringAsync(ApiRoutes.FuelNewsSubscription));
    Check(preferences.RootElement.GetProperty("data").GetProperty("enabled").GetBoolean(), "Opt-in did not persist.");
    Check(!preferences.RootElement.GetProperty("data").GetProperty("includeForecasts").GetBoolean(), "Forecasts were enabled without consent.");
    http.DefaultRequestHeaders.Authorization = new("Bearer", otherAccess);
    http.DefaultRequestHeaders.Remove("X-App-Instance-Id");
    http.DefaultRequestHeaders.Add("X-App-Instance-Id", otherOwner);
    using var takeover = await http.PutAsJsonAsync(ApiRoutes.FuelNewsSubscription, body);
    Check(takeover.StatusCode == HttpStatusCode.BadRequest, "Another identity claimed an existing device token.");
    http.DefaultRequestHeaders.Authorization = new("Bearer", access);
    http.DefaultRequestHeaders.Remove("X-App-Instance-Id");
    http.DefaultRequestHeaders.Add("X-App-Instance-Id", owner);
    using var optOut = await http.DeleteAsync(ApiRoutes.FuelNewsSubscription);
    Check(optOut.StatusCode == HttpStatusCode.OK, "Opt-out failed.");
    http.DefaultRequestHeaders.Authorization = null;
    using var unauthorized = await http.DeleteAsync(ApiRoutes.FuelNewsSubscription);
    Check(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "Anonymous notification mutation accepted.");
    Console.WriteLine("PASS native subscription opt-in/out, confirmed-only defaults, authentication and token ownership");
}
finally { await app.StopAsync(); }

static void Check(bool success, string message) { if (!success) throw new Exception(message); }
sealed class TestClock(DateTime now) : TimeProvider
{
    public DateTime Now = now;
    public override DateTimeOffset GetUtcNow() => new(Now);
}
sealed class FakeClients(FakePush handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://push.example.test/") };
}
sealed class FakePush : HttpMessageHandler
{
    public int Sends;
    public bool FailNext;
    public bool InvalidDevice;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        CheckHost(request.RequestUri!);
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        if (request.RequestUri!.AbsolutePath.EndsWith("send"))
        {
            Sends++;
            if (FailNext) { FailNext = false; return new(HttpStatusCode.ServiceUnavailable); }
            if (!document.RootElement.GetProperty("data").TryGetProperty("newsId", out _)) throw new Exception("Missing article link.");
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = new { status = "ok", id = "ticket-fixture" } }) };
        }
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = new Dictionary<string, object>
            { ["ticket-fixture"] = InvalidDevice ? new { status = "error", details = new { error = "DeviceNotRegistered" } } : new { status = "ok", details = new { error = "" } } } }) };
    }
    private static void CheckHost(Uri uri) { if (uri.Host != "push.example.test") throw new Exception("Unexpected push host."); }
}
