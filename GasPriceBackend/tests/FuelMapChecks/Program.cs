using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Isolated HTTP + EF InMemory fixtures. Never reads production configuration or calls external services.
var root = args.Length == 2 && args[0] == "--geography-root"
    ? Path.GetFullPath(args[1]) : Path.GetFullPath("GasPriceBackend");
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
    { Args = ["--hostBuilder:reloadConfigOnChange=false"], ContentRootPath = root });
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase("fuel-map-" + Environment.ProcessId));
builder.Services.AddBrowserAuthentication();
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<FuelMapGeography>();
builder.Services.AddScoped<FuelMapPriceService>();
await using var app = builder.Build();
app.MapFuelMapEndpoints(new AuthService(), "map-fixture");
var geo = app.Services.GetRequiredService<FuelMapGeography>();
Check(geo.Cities.Count == 1642 && geo.Index.Files.Length == 93, "National catalog coverage");
Check(geo.Cities["1380600000"].ProvinceId is null && geo.Cities["0730600000"].CityClass == "HUC" &&
    geo.Cities["0730600000"].ProvinceId is null && geo.Cities["1908703000"].ProvinceId is null &&
    geo.Cities["1381701000"].Level == "Mun", "NCR, Pateros, HUC/ICC metadata");
var now = DateTime.UtcNow;
var today = DateOnly.FromDateTime(now.AddHours(8));
DoeFuelPrice Row(string city = "Manila", string province = "Metro Manila", string grade = "RON 91", decimal min = 58, decimal max = 62) => new()
{
    City = city, CityKey = DoeFuelPriceImporter.Normalize(city), Province = province,
    ProvinceKey = DoeFuelPriceImporter.Normalize(province), Region = null, OilCompany = "Fixture",
    FuelGrade = grade, MinPricePerLiter = min, MaxPricePerLiter = max,
    WeekStart = today.AddDays(-2), WeekEnd = today, SourceUrl = "https://doe.gov.ph/fixture.pdf", FetchedAtUtc = now.AddMinutes(-1)
};
var sample = new[] { Row(), Row(grade: "DIESEL", min: 50, max: 55),
    Row(grade: "RON 95", min: 1, max: 99), Row(grade: "DIESEL PLUS", min: 1, max: 99),
    Row(city: "", min: 1, max: 99), Row(city: "Quezon City", min: 70, max: 80) };
var imusId = geo.Cities.Values.Single(city => city.Name == "City of Imus" && city.GroupName == "Cavite").Id;
var imusRows = new[] { Row("Imus City", "Cavite"), Row("Imus City", "Cavite", "DIESEL") };
foreach (var row in imusRows)
{
    row.Region = "Region IV-A";
    row.SourceUrl = "https://d24qbtp4vooyzi.cloudfront.net/api/media/file/CALABARZON.pdf?prefix=dev%2Fmedia";
}
var untrusted = Row("Imus City", "Cavite");
untrusted.SourceUrl = "https://another-distribution.cloudfront.net/report.pdf";
Check(FuelMapPriceService.BuildFeed([imusId], [untrusted], geo, now).Items[0].Status == "unavailable",
    "Unrelated CDN distributions cannot supply city prices");
var feed = FuelMapPriceService.BuildFeed(["1380600000", "1380200000"], sample, geo, now);
Check(feed.Items[0].Gasoline?.MinPrice == 58 && feed.Items[0].Diesel?.MinPrice == 50 &&
    feed.Items[0].Status == "available" && feed.Items[1].Status == "unavailable" &&
    feed.Items.All(item => item.Scope == "city"), "Grade isolation and no provincial/neighbor fallback");
Check(FuelMapPriceService.BuildFeed(["1380600000"], [Row()], geo, now).Items[0].Status == "partial", "Missing grade remains partial");
var old = Row(); old.WeekStart = today.AddDays(-40); old.WeekEnd = today.AddDays(-34);
Check(FuelMapPriceService.BuildFeed(["1380600000"], [old], geo, now).Items[0].Status == "unavailable", "Expired rows excluded");
var previous = Row(grade: "DIESEL"); previous.WeekStart = today.AddDays(-3); previous.WeekEnd = today.AddDays(-1);
Check(FuelMapPriceService.BuildFeed(["1380600000"], [Row(), previous], geo, now).Items[0].Diesel is null, "Do not combine report weeks");
var otherReport = Row(grade: "DIESEL"); otherReport.SourceUrl = "https://doe.gov.ph/other.pdf";
Check(FuelMapPriceService.BuildFeed(["1380600000"], [Row(), otherReport], geo, now).Items[0].Status == "partial", "Do not combine reports");
var duplicate = Row(min: 59, max: 61); duplicate.FetchedAtUtc = now;
Check(FuelMapPriceService.BuildFeed(["1380600000"], [Row(), duplicate], geo, now).Items[0].Gasoline?.MinPrice == 59, "Latest company observation wins");
var secondCompany = Row(min: 57, max: 64); secondCompany.OilCompany = "Other";
Check(FuelMapPriceService.BuildFeed(["1380600000"], [Row(), secondCompany], geo, now).Items[0].Gasoline is { MinPrice: 57, MaxPrice: 64 }, "Aggregate company ranges");
var legacy = Row(city: ""); legacy.CityKey = "MANILA";
Check(geo.Resolve(legacy)?.Id == "1380600000" && geo.Resolve(Row(city: "Cebu City", province: "Cebu"))?.Id == "0730600000" &&
    geo.Resolve(Row(city: "Cotabato City", province: "Maguindanao"))?.Id == "1908703000", "Legacy city keys, HUC and historical province aliases");
var cutoff = Row(); cutoff.WeekStart = today.AddDays(-36); cutoff.WeekEnd = today.AddDays(-30);
var nearMidnight = today.ToDateTime(new TimeOnly(15, 59), DateTimeKind.Utc);
var cutoffFeed = FuelMapPriceService.BuildFeed(["1380600000"], [cutoff], geo, nearMidnight);
Check(cutoffFeed.ExpiresAtUtc == today.ToDateTime(new TimeOnly(16, 0), DateTimeKind.Utc), "Expiry capped at Philippine eligibility midnight");
Check(FuelMapPriceService.BuildFeed(["1380600000"], [cutoff], geo, nearMidnight.AddMinutes(2)).Items[0].Status == "unavailable", "Midnight freshness transition");
Console.WriteLine("PASS city identity, grades, scope, report selection, aggregation and freshness fixtures");

var estimateNow = new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);
var estimateToday = new DateOnly(2026, 11, 18);
var baseline = new[] { Row("Imus City", "Cavite", min: 60, max: 65), Row("Imus City", "Cavite", "DIESEL", 50, 55) };
foreach (var row in baseline)
{
    row.WeekStart = new(2026, 11, 3); row.WeekEnd = new(2026, 11, 9);
    row.FetchedAtUtc = estimateNow.AddDays(-9);
}
FuelMapAdjustment Change(string fuel, int day, decimal amount, string? company = null) =>
    new(fuel, company, new(2026, 11, day), new(2026, 11, day, 0, 0, 0, DateTimeKind.Utc), amount, ["https://doe.gov.ph/notice.pdf"]);
var changes = new[] { Change("gasoline", 10, 2), Change("gasoline", 17, -1), Change("diesel", 10, -2), Change("diesel", 17, 3) };
FuelMapCityPrices Estimated(params FuelMapAdjustment[] supplied) =>
    FuelMapPriceService.BuildFeed([imusId], baseline, geo, estimateNow, supplied).Items[0];
var calculated = Estimated(changes);
Check(calculated.PriceKind == "estimated" && !calculated.IsOutdated && calculated.AsOf == estimateToday &&
    calculated.Gasoline is { MinPrice: 61, MaxPrice: 66 } && calculated.Diesel is { MinPrice: 51, MaxPrice: 56 } &&
    calculated.ReportedGasoline?.MinPrice == 60 && calculated.WeekEnd == new DateOnly(2026, 11, 9),
    "Estimate applies signed per-fuel adjustments and preserves original report prices/dates");
Check(Estimated(changes.Concat(changes).ToArray()).Gasoline?.MinPrice == 61, "Duplicate sources do not double count changes");
Check(Estimated(changes.Where(c => c.EffectiveDate.Day != 10).ToArray()).PriceKind == "doe_reported" &&
    Estimated().IsOutdated, "Missing weekly evidence keeps original outdated DOE prices");
Check(Estimated(changes.Append(Change("gasoline", 17, 9)).ToArray()).PriceKind == "doe_reported",
    "Conflicting confirmed amounts prevent estimation");
Check(Estimated(changes.Select(c => c with { OilCompany = "Another company" }).ToArray()).PriceKind == "doe_reported",
    "A different company's notices cannot update a city observation");
Check(Estimated(changes.Select(c => c with { ChangePerLiter = 0 }).ToArray()).PriceKind == "estimated",
    "Explicit zero changes provide weekly coverage");
var otherCompanyRow = Row("Imus City", "Cavite", min: 40, max: 45);
otherCompanyRow.WeekStart = baseline[0].WeekStart; otherCompanyRow.WeekEnd = baseline[0].WeekEnd;
otherCompanyRow.FetchedAtUtc = baseline[0].FetchedAtUtc; otherCompanyRow.OilCompany = "Other";
var companyChanges = changes.Select(c => c with { OilCompany = "Fixture" }).Concat(new[] {
    Change("gasoline", 10, 5, "Other"), Change("gasoline", 17, 2, "Other") }).ToArray();
var companyFeed = FuelMapPriceService.BuildFeed([imusId], baseline.Append(otherCompanyRow), geo, estimateNow, companyChanges).Items[0];
Check(companyFeed.Gasoline is { MinPrice: 47, MaxPrice: 66 },
    "Company-specific changes are applied before combining the city range");
Check(Estimated(changes.Concat(changes.Select(c => c with { OilCompany = "Fixture" })).ToArray()).Adjustments.Length == 4,
    "National and company corroboration yields one applied change per company/fuel/date");
var futureChange = Change("gasoline", 24, 10);
Check(Estimated(changes.Append(futureChange).ToArray()).Gasoline?.MinPrice == 61, "Future adjustments are excluded");
var duringReport = Change("gasoline", 3, 10);
Check(Estimated(changes.Append(duringReport).ToArray()).Gasoline?.MinPrice == 61,
    "Changes within the report period are not counted again");
var nearEffective = FuelMapPriceService.BuildFeed([imusId], baseline, geo,
    new DateTime(2026, 11, 16, 23, 58, 0, DateTimeKind.Utc), changes).Items[0];
Check(nearEffective.ExpiresAtUtc == new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc),
    "Map cache expires at a confirmed effective time");
var newsArticle = new FuelNewsContent("fixture-change", "fixture-topic", "adjustment", "confirmed", "Fixture", "Fixture", "Fixture",
    new(2026, 11, 17), null, estimateNow.AddDays(-1), [new("gasoline", null, 1, 1, "confirmed")],
    [new("Fixture", "https://example.com/confirmed", estimateNow.AddDays(-2))]);
var newsRow = new FuelNews { Category = "adjustment", Status = "confirmed", PublishedAtUtc = estimateNow.AddDays(-2),
    UpdatedAtUtc = estimateNow.AddDays(-2), ContentJson = JsonSerializer.Serialize(newsArticle, FuelNewsValidation.Json) };
Check(FuelMapEstimates.Evidence([], [newsRow], estimateNow) is [{ ChangePerLiter: 1 }],
    "Expired confirmed news remains historical adjustment evidence");
newsRow.ContentJson = JsonSerializer.Serialize(newsArticle with { Status = "forecast" }, FuelNewsValidation.Json);
Check(FuelMapEstimates.Evidence([], [newsRow], estimateNow).Length == 0, "Forecast articles cannot supply estimates");
newsRow.ContentJson = JsonSerializer.Serialize(newsArticle, FuelNewsValidation.Json);
newsRow.SupersededById = Guid.NewGuid();
Check(FuelMapEstimates.Evidence([], [newsRow], estimateNow).Length == 0, "Superseded news cannot supply estimates");
Console.WriteLine("PASS map estimate baselines, signed changes, provenance, missing/conflicting evidence and effective-time expiry");

const string installation = "4cb21d7c-932a-4796-9615-d7b05cfe7fab";
var auth = new AuthService();
var token = auth.GenerateSecret();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Guests.Add(new Guest { AppInstanceId = installation, PlayerId = 123456789012345, PlayerName = "Map fixture", IsLoggedIn = true,
        CreatedAt = now, LastLoginAt = now, AccessTokenHash = auth.HashSecret(token), AccessTokenExpiresAt = now.AddHours(1) });
    db.DoeFuelPrices.AddRange(sample);
    db.DoeFuelPrices.AddRange(imusRows);
    var tanza = new[] { Row("Tanza", "Cavite"), Row("Tanza", "Cavite", "DIESEL", 50, 55) };
    foreach (var row in tanza) { row.WeekStart = today.AddDays(-15); row.WeekEnd = today.AddDays(-9); }
    db.DoeFuelPrices.AddRange(tanza);
    for (var day = today.AddDays(-8); day <= today; day = day.AddDays(1))
        if (day.DayOfWeek == DayOfWeek.Tuesday)
            db.FuelAdjustments.Add(new FuelAdjustment { WeekStart = day, WeekEnd = day.AddDays(6),
                OilCompany = "Fixture", EffectiveDatePhilippines = day,
                EffectiveAtUtc = FuelMapEstimates.StartUtc(day), GasolineChangePerLiter = 1, DieselChangePerLiter = -1,
                SourceUrl = "https://doe.gov.ph/notice.pdf", FetchedAtUtc = now.AddMinutes(-1) });
    await db.SaveChangesAsync();
}
await app.StartAsync();
try
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var http = new HttpClient { BaseAddress = new Uri(address) };
    using var index = await http.GetAsync(ApiRoutes.FuelMapIndex);
    Check(index.StatusCode == HttpStatusCode.OK && index.Headers.CacheControl?.Public == true && index.Headers.ETag is not null,
        "Public index and caching");
    using var json = JsonDocument.Parse(await index.Content.ReadAsStringAsync());
    Check(json.RootElement.GetProperty("data").GetProperty("cities").GetArrayLength() == 1642, "Index response envelope");
    var path = ApiRoutes.FuelMapBoundary.Replace("{version}", FuelMapGeography.Version).Replace("{fileId}", "1303900000-01");
    using var request = new HttpRequestMessage(HttpMethod.Get, path);
    request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
    using var boundary = await http.SendAsync(request);
    Check(boundary.StatusCode == HttpStatusCode.OK && boundary.Headers.CacheControl?.Extensions.Any(e => e.Name == "immutable") == true &&
        boundary.Content.Headers.ContentEncoding.Contains("gzip"), "Compressed immutable boundary");
    using var uncompressed = new System.IO.Compression.GZipStream(await boundary.Content.ReadAsStreamAsync(), System.IO.Compression.CompressionMode.Decompress);
    using var boundaryJson = await JsonDocument.ParseAsync(uncompressed);
    Check(boundaryJson.RootElement.GetProperty("data").GetProperty("features")[0].GetProperty("id").GetString() == "1380600000", "Independently identified polygon");
    using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
    conditional.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
    conditional.Headers.TryAddWithoutValidation("If-None-Match", "W/" + boundary.Headers.ETag);
    using var unchanged = await http.SendAsync(conditional);
    Check(unchanged.StatusCode == HttpStatusCode.NotModified && (await unchanged.Content.ReadAsByteArrayAsync()).Length == 0, "Conditional boundary request");
    using var noGzip = new HttpRequestMessage(HttpMethod.Get, path);
    noGzip.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip;q=0");
    using var identity = await http.SendAsync(noGzip);
    Check(identity.Content.Headers.ContentEncoding.Count == 0 && identity.Headers.ETag != boundary.Headers.ETag, "Encoding negotiation and representation ETags");
    foreach (var invalid in new[] { path.Replace("ph-2026q3-v1", "unknown"), path.Replace("1303900000-01", "9999999999-01") })
        Check((await http.GetAsync(invalid)).StatusCode == HttpStatusCode.NotFound, "Unknown geographic file/version");
    Check((await http.GetAsync(ApiRoutes.FuelMapIndex + "?extra=1")).StatusCode == HttpStatusCode.BadRequest, "Index query validation");

    string Body(params string[] ids) => JsonSerializer.Serialize(new { boundaryVersion = FuelMapGeography.Version, cityIds = ids });
    async Task<HttpResponseMessage> Post(string body, bool credentials = true, string contentType = "application/json", bool instance = true, string? bearer = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.FuelMapPrices)
            { Content = new StringContent(body, Encoding.UTF8, contentType) };
        if (credentials) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (bearer ?? token));
        if (instance) req.Headers.TryAddWithoutValidation("X-App-Instance-Id", installation);
        return await http.SendAsync(req);
    }
    using var result = await Post(Body("1380600000", "1380200000", "1380600000", imusId));
    Check(result.StatusCode == HttpStatusCode.OK && result.Headers.CacheControl?.NoStore == true, "Authenticated batch read");
    using var prices = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
    var items = prices.RootElement.GetProperty("data").GetProperty("items");
    Check(items.GetArrayLength() == 3 && items[0].GetProperty("gasoline").GetProperty("minPrice").GetDecimal() == 58 &&
        items[1].GetProperty("status").GetString() == "unavailable", "Batch query, ordering, deduplication and missing data");
    Check(items[2].GetProperty("cityId").GetString() == imusId &&
        items[2].GetProperty("status").GetString() == "available" &&
        items[2].GetProperty("diesel").GetProperty("minPrice").GetDecimal() == 58,
        "Map batch accepts Imus DOE reports served by the importer's approved CDN");
    Check(items[2].GetProperty("priceKind").GetString() == "doe_reported" &&
        items[2].GetProperty("reportedGasoline").GetProperty("minPrice").GetDecimal() == 58,
        "HTTP exposes DOE reported identity and unmodified baseline");
    var tanzaId = geo.Cities.Values.Single(city => city.Name == "Tanza" && city.GroupName == "Cavite").Id;
    using var estimateResponse = await Post(Body(tanzaId));
    using var estimateJson = JsonDocument.Parse(await estimateResponse.Content.ReadAsStringAsync());
    var estimatedItem = estimateJson.RootElement.GetProperty("data").GetProperty("items")[0];
    Check(estimateResponse.StatusCode == HttpStatusCode.OK && estimatedItem.GetProperty("priceKind").GetString() == "estimated" &&
        estimatedItem.GetProperty("adjustments").GetArrayLength() > 0 &&
        estimatedItem.GetProperty("reportedGasoline").GetProperty("minPrice").GetDecimal() == 58,
        "HTTP queries stored confirmed notices and returns estimates with baseline and evidence");
    foreach (var body in new[] { "{", "[]", "{}", Body(), Body("9999999999"), Body("138060000"),
        Body(Enumerable.Repeat("1380600000", 2001).ToArray()), new string(' ', 32769),
        Body("1380600000").Replace("\"cityIds\":", "\"extra\":0,\"cityIds\":"),
        Body("1380600000").Replace("\"cityIds\":", "\"cityIds\":[],\"cityIds\":"),
        Body("1380600000").Replace("ph-2026q3-v1", "old"),
        Body("1380600000").Replace("\"1380600000\"", "1380600000") })
    {
        using var rejected = await Post(body);
        Check(rejected.StatusCode == HttpStatusCode.BadRequest, "Reject invalid body: " + body[..Math.Min(body.Length, 90)]);
        using var error = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Check(error.RootElement.GetProperty("code").GetInt32() == 400, "Shared validation envelope");
    }
    Check((await Post(Body("1380600000"), contentType: "text/plain")).StatusCode == HttpStatusCode.BadRequest, "Content type");
    Check((await Post(Body("1380600000"), credentials: false)).StatusCode == HttpStatusCode.Unauthorized, "Missing credentials");
    Check((await Post(Body("1380600000"), instance: false)).StatusCode == HttpStatusCode.Unauthorized, "Missing installation");
    Check((await Post(Body("1380600000"), bearer: "invalid")).StatusCode == HttpStatusCode.Unauthorized, "Invalid token");
    using var cookieRequest = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.FuelMapPrices)
        { Content = new StringContent(Body("1380600000"), Encoding.UTF8, "application/json") };
    cookieRequest.Headers.TryAddWithoutValidation("Cookie", BrowserAuthentication.AccessCookie + "=" + token + "; " + AuthEndpoints.AppInstanceIdCookie + "=" + installation);
    Check((await http.SendAsync(cookieRequest)).StatusCode == HttpStatusCode.BadRequest, "Cookie-authenticated POST requires CSRF");
    using var all = await Post(Body(geo.Cities.Keys.ToArray()));
    Check(all.StatusCode == HttpStatusCode.OK, "Entire national inventory fits batch and body limits");
    Console.WriteLine("PASS HTTP geometry/envelopes/gzip/ETags, authenticated batch query, limits, validation and browser CSRF enforcement");
}
finally { await app.StopAsync(); }

// Exercise the real middleware separately, with no database or authentication involved.
var rateBuilder = WebApplication.CreateSlimBuilder();
rateBuilder.Logging.ClearProviders();
rateBuilder.WebHost.UseUrls("http://127.0.0.1:0");
rateBuilder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
await using var rateApp = rateBuilder.Build();
rateApp.UseMiddleware<RateLimitMiddleware>();
rateApp.MapPost(ApiRoutes.FuelMapPrices, () => Results.Ok());
rateApp.MapGet(ApiRoutes.FuelMapBoundary, () => Results.Ok());
await rateApp.StartAsync();
try
{
    var address = rateApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var http = new HttpClient { BaseAddress = new Uri(address) };
    Check((await http.PostAsync(ApiRoutes.FuelMapPrices, null)).StatusCode == HttpStatusCode.OK, "Initial price permit");
    using var limited = await http.PostAsync(ApiRoutes.FuelMapPrices, null);
    Check(limited.StatusCode == HttpStatusCode.TooManyRequests && limited.Headers.RetryAfter is not null, "Batch cooldown");
    for (var i = 0; i < 120; i++)
        Check((await http.GetAsync($"/fuel-prices/map/geography/{FuelMapGeography.Version}/{i:D10}-01")).StatusCode == HttpStatusCode.OK, "Concurrent chunk budget");
    Check((await http.GetAsync($"/fuel-prices/map/geography/{FuelMapGeography.Version}/9999999999-01")).StatusCode == HttpStatusCode.TooManyRequests,
        "Changing chunk IDs cannot bypass shared minute budget");
    Console.WriteLine("PASS price cooldown and shared boundary burst rate limits");
}
finally { await rateApp.StopAsync(); }
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
