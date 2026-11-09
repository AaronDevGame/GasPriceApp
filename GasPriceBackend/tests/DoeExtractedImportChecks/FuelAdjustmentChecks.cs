using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class FuelAdjustmentChecks
{
    private const string Valid = """
        {"sourceUrl":"https://doe.gov.ph/adjustments.pdf","weekStart":"2026-10-27","weekEnd":"2026-11-02",
         "rows":[{"oilCompany":" PETRON ","effectiveDatePhilippines":"2026-10-27",
         "effectiveTimePhilippines":"06:00:00","gasolineChangePerLiter":-1.25,
         "dieselChangePerLiter":0.50,"keroseneChangePerLiter":null}]}
        """;

    public static async Task RunAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = ["--hostBuilder:reloadConfigOnChange=false"], EnvironmentName = "Production",
            ContentRootPath = Path.GetFullPath("GasPriceBackend")
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["ADMIN_API_KEY"] = "test-admin";
        builder.Configuration["NEWS_API_KEY"] = "test-news";
        builder.Configuration["CODEX_ADMIN_API_KEY"] = "test-codex";
        builder.Configuration["OPENAI_API_KEY"] = null;
        var clock = new AdjustmentClock();
        builder.Services.AddSingleton<TimeProvider>(clock);
        // Valid submissions stop at the connection boundary; no database/network is needed for validation checks.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=never_connect;Username=test")
            .AddInterceptors(new StopDatabaseConnection()).Options;
        builder.Services.AddScoped(_ => new AppDbContext(options));
        await using var app = builder.Build();
        app.UseMiddleware<RateLimitMiddleware>();
        app.UseMiddleware<AdminAuthMiddleware>();
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (ValidatedSubmissionReachedDatabase) { context.Response.StatusCode = 204; }
        });
        app.MapFuelAdjustmentExtractedImportEndpoints("test-instance");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };

        async Task<HttpResponseMessage> Post(string body, string type = "application/json",
            string query = "", bool chunked = false, bool advance = true)
        {
            if (advance) clock.Advance();
            using var message = new HttpRequestMessage(HttpMethod.Post,
                ApiRoutes.AdminFuelPriceAdjustmentsImportExtracted + query);
            message.Content = chunked ? new ChunkedBody(body) : new StringContent(body, Encoding.UTF8, type);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-codex");
            message.Headers.ConnectionClose = true;
            message.Headers.ExpectContinue = true;
            return await client.SendAsync(message);
        }
        foreach (var invalid in new[]
        {
            "null", "[]", "{", Valid.Replace("\"sourceUrl\":", "\"unknown\":1,\"sourceUrl\":"),
            Valid.Replace("\"rows\":", "\"rows\":[],\"rows\":"),
            Valid.Replace("\"sourceUrl\":\"https://doe.gov.ph/adjustments.pdf\"", "\"sourceUrl\":null"),
            Valid.Replace("https://doe.gov.ph/adjustments.pdf", "https://example.com/report.pdf"),
            Valid.Replace("https://doe.gov.ph/adjustments.pdf", FuelAdjustmentImporter.DoePage),
            Valid.Replace("\"weekStart\":\"2026-10-27\",", ""),
            Valid.Replace("2026-10-27", "2026-10-28"),
            Valid.Replace("2026-11-02", "2026-11-03"),
            Valid.Replace("2026-10-27", "9999-12-28"),
            Valid.Replace("\"effectiveDatePhilippines\":\"2026-10-27\"", "\"effectiveDatePhilippines\":\"2026-11-03\""),
            Valid.Replace("\"06:00:00\"", "\"24:00:00\""),
            Valid.Replace("\" PETRON \"", "null"), Valid.Replace("\" PETRON \"", "\" \""),
            Valid.Replace("\"oilCompany\":", "\"unknown\":1,\"oilCompany\":"),
            Valid.Replace("\"oilCompany\":", "\"oilCompany\":\"SHELL\",\"oilCompany\":"),
            Valid.Replace("-1.25", "\"-1.25\""), Valid.Replace("-1.25", "-100.01"),
            Valid.Replace("-1.25", "1.001"),
            Valid.Replace("\"keroseneChangePerLiter\":null", "\"keroseneChangePerLiter\":true"),
            Valid.Replace(",\"keroseneChangePerLiter\":null", ""),
            Valid.Replace("-1.25", "null").Replace("0.50", "null"),
            WithRows("null"), WithRows("[]"), WithRows("[null]"),
            WithRows("[" + string.Join(",", Enumerable.Repeat(Row(), 101)) + "]"),
            WithRows("[" + Row() + "," + Row().Replace(" PETRON ", "petron") + "]"),
            new string(' ', 128 * 1024 + 1)
        })
        {
            using var response = await Post(invalid);
            Check(response.StatusCode == HttpStatusCode.BadRequest, "Reject invalid adjustment JSON before database access.");
            using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Check(envelope.RootElement.GetProperty("instanceId").GetString() == "test-instance", "Use the shared response envelope.");
        }
        using (var response = await Post(Valid, type: "text/plain"))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "Require JSON content type.");
        using (var response = await Post(Valid, query: "?sourceUrl=ignored"))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "Reject query parameters.");
        using (var response = await Post(new string(' ', 128 * 1024 + 1), chunked: true))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "Bound chunked bodies.");
        foreach (var valid in new[]
        {
            Valid, Valid.Replace("\"06:00:00\"", "null"),
            Valid.Replace("-1.25", "0"), Valid.Replace("-1.25", "-100").Replace("0.50", "100"),
            Valid.Replace("https://doe.gov.ph/adjustments.pdf", "https://d24qbtp4vooyzi.cloudfront.net/adjustments.pdf"),
            WithRows("[" + Row() + "," + Row().Replace("06:00:00", "12:00:00") + "]")
        })
        {
            using var response = await Post(valid);
            Check(response.StatusCode == HttpStatusCode.NoContent, "Valid adjustments must reach the database boundary.");
        }
        using (var response = await Post(Valid, advance: false))
            Check(response.StatusCode == HttpStatusCode.TooManyRequests && response.Headers.RetryAfter is not null,
                "Extracted adjustments must share the admin rate limit.");
        await app.StopAsync();
        Console.WriteLine("PASS: extracted adjustment HTTP validation, body limits, accepted shapes and admin cooldown (no network)");
        await CheckPersistenceAsync(clock);
    }

    private static string Row()
    {
        using var document = JsonDocument.Parse(Valid);
        return document.RootElement.GetProperty("rows")[0].GetRawText();
    }
    private static string WithRows(string rows) => Valid[..Valid.IndexOf("\"rows\":", StringComparison.Ordinal)] + "\"rows\":" + rows + "}";
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static async Task CheckPersistenceAsync(AdjustmentClock clock)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("DOE_TEST_PGPORT"), out var port))
        {
            Console.WriteLine("SKIP: adjustment PostgreSQL upsert checks (set DOE_TEST_PGPORT for a disposable local cluster)");
            return;
        }
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(
            $"Host=127.0.0.1;Port={port};Database=gasprice_adjustments_submitted_test;Username={Environment.UserName}").Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();
        await db.FuelAdjustments.ExecuteDeleteAsync();
        // Exercise the real endpoint against a disposable database with no extractor or HTTP clients registered.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = ["--hostBuilder:reloadConfigOnChange=false"], EnvironmentName = "Production",
            ContentRootPath = Path.GetFullPath("GasPriceBackend")
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["ADMIN_API_KEY"] = "test-admin";
        builder.Configuration["CODEX_ADMIN_API_KEY"] = "test-codex";
        builder.Configuration["OPENAI_API_KEY"] = null;
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddScoped(_ => new AppDbContext(options));
        await using var app = builder.Build();
        app.UseMiddleware<AdminAuthMiddleware>();
        app.MapFuelAdjustmentExtractedImportEndpoints("test-instance");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-codex");
        async Task<JsonElement> Submit(string body)
        {
            clock.Advance();
            using var response = await client.PostAsync(ApiRoutes.AdminFuelPriceAdjustmentsImportExtracted,
                new StringContent(body, Encoding.UTF8, "application/json"));
            Check(response.StatusCode == HttpStatusCode.OK, "Valid extracted adjustments must save.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("data").Clone();
        }
        var first = await Submit(Valid);
        var saved = await db.FuelAdjustments.AsNoTracking().SingleAsync();
        Check(first.GetProperty("added").GetInt32() == 1 && saved.OilCompany == "PETRON" &&
            saved.GasolineChangePerLiter == -1.25m && saved.KeroseneChangePerLiter is null &&
            saved.EffectiveAtUtc == new DateTime(2026, 10, 26, 22, 0, 0, DateTimeKind.Utc),
            "Persist normalized amounts and convert Philippine time to UTC.");
        var repeat = await Submit(Valid);
        Check(repeat.GetProperty("status").GetString() == "already_imported" &&
            (await db.FuelAdjustments.AsNoTracking().SingleAsync()).FetchedAtUtc == saved.FetchedAtUtc,
            "Retries must preserve unchanged timestamps.");
        var correction = await Submit(Valid.Replace("-1.25", "-1.50"));
        Check(correction.GetProperty("updated").GetInt32() == 1 && await db.FuelAdjustments.CountAsync() == 1 &&
            (await db.FuelAdjustments.AsNoTracking().SingleAsync()).GasolineChangePerLiter == -1.50m,
            "Corrections must update without duplicating rows.");
        await Submit(Valid.Replace(" PETRON ", "SHELL").Replace("\"06:00:00\"", "null"));
        Check(await db.FuelAdjustments.CountAsync() == 2 &&
            (await db.FuelAdjustments.AsNoTracking().SingleAsync(a => a.OilCompany == "SHELL")).EffectiveAtUtc is null,
            "Date-only rows retain a null UTC time and omitted companies remain stored.");
        await app.StopAsync();
        Console.WriteLine("PASS: extracted adjustment PostgreSQL HTTP inserts, retries, corrections, UTC conversion and partial upserts");
    }

    private sealed class AdjustmentClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now = _now.AddSeconds(2);
    }
    private sealed class ValidatedSubmissionReachedDatabase : Exception;
    private sealed class StopDatabaseConnection : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            => throw new ValidatedSubmissionReachedDatabase();
    }
    private sealed class ChunkedBody(string body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(Encoding.UTF8.GetBytes(body)).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
