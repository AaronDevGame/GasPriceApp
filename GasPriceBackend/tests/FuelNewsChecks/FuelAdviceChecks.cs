using System.Text.Json;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class FuelAdviceChecks
{
    public static async Task RunHttpValidationAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = ["--hostBuilder:reloadConfigOnChange=false"],
            ContentRootPath = Path.GetFullPath("GasPriceBackend")
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        // Invalid requests must return before any database access. Never use configured application credentials.
        builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(
            "Host=127.0.0.1;Port=1;Database=advice_validation_fixture;Username=fixture;Timeout=1"));
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddScoped<FuelAdviceService>();
        await using var app = builder.Build();
        app.MapFuelAdviceEndpoints("advice-validation-fixture");
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            foreach (var query in new[] { "", "?fuelType=", "?fuelType=petrol", "?fuelType=Gasoline",
                "?fuelType=gasoline&fuelType=diesel", "?fuelType=gasoline&unknown=1",
                "?fuelType=gasoline&oilCompany=", "?fuelType=gasoline&oilCompany=%20Shell",
                "?fuelType=gasoline&oilCompany=%0aShell", "?fuelType=gasoline&oilCompany=A&oilCompany=B",
                "?fuelType=gasoline&oilCompany=" + new string('a', 101) })
            {
                using var response = await http.GetAsync(ApiRoutes.FuelAdvice + query);
                if (response.StatusCode != HttpStatusCode.BadRequest || response.Headers.CacheControl?.NoStore != true)
                    throw new InvalidOperationException("Invalid advice query was not rejected with no-store: " + query);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (json.RootElement.GetProperty("code").GetInt32() != 400)
                    throw new InvalidOperationException("Missing shared error envelope");
            }
            Console.WriteLine("PASS fuel advice HTTP parameter validation and no-store/error envelope checks");
        }
        finally { await app.StopAsync(); }
    }

    public static void Run()
    {
        // November 10, 06:00 in the Philippines. These are fixtures, not real price figures.
        var effective = new DateTime(2026, 11, 9, 22, 0, 0, DateTimeKind.Utc);
        var date = new DateOnly(2026, 11, 10);
        var rollback = new FuelAdviceEvidence("confirmed", date, effective, -2, -2, null,
            Guid.NewGuid(), null, ["https://example.com/fixture"], effective.AddDays(20));
        FuelAdviceResponse Advice(DateTime now, params FuelAdviceEvidence[] rows) =>
            FuelAdviceCalculator.Calculate("gasoline", null, now, rows);
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
        }
        Check(Advice(effective.AddTicks(-1), rollback).Status == FuelAdviceStatus.Wait, "Rollback before boundary");
        var applied = Advice(effective, rollback);
        Check(applied.Status == FuelAdviceStatus.GasNow && applied.Reason == "rollback_effective", "Rollback at exact boundary");
        Check(applied.ValidUntilUtc == effective.AddDays(7), "Hard seven-day validity");
        Check(Advice(effective.AddDays(7), rollback).Status == FuelAdviceStatus.Unknown, "Old rollback expires at boundary");
        Check(Advice(effective, rollback with { ValidUntilUtc = effective }).Status == FuelAdviceStatus.Unknown, "Article expiry boundary");
        var increase = rollback with { MinChangePerLiter = 2, MaxChangePerLiter = 2 };
        Check(Advice(effective.AddMinutes(-1), increase).Status == FuelAdviceStatus.GasNow, "Increase upcoming");
        Check(Advice(effective, increase).Status == FuelAdviceStatus.Neutral, "Increase effective");
        Check(Advice(effective, rollback with { MinChangePerLiter = 0, MaxChangePerLiter = 0 }).Status == FuelAdviceStatus.Neutral,
            "Zero adjustment");
        var forecast = rollback with { ConfirmationStatus = "forecast", MinChangePerLiter = -3 };
        var onlyForecast = Advice(effective.AddHours(-1), forecast);
        Check(onlyForecast.Status == FuelAdviceStatus.Unknown && onlyForecast.Forecast is not null &&
            onlyForecast.ValidUntilUtc == effective, "Forecast never gives confirmed advice and expires at effective time");
        Check(Advice(effective, forecast).Forecast is null, "Past forecast is not upcoming");
        Check(Advice(effective).Reason == "no_confirmed_adjustment", "Empty evidence");
        Check(Advice(effective, rollback, increase).Reason == "conflicting_adjustments", "Conflicting amounts");
        Check(Advice(effective, rollback, rollback with { EffectiveAtUtc = effective.AddHours(1) }).Status == FuelAdviceStatus.Unknown,
            "Conflicting exact effective times");
        var dateOnly = rollback with { EffectiveAtUtc = null };
        var start = FuelAdviceCalculator.StartUtc(date);
        Check(Advice(start.AddTicks(-1), dateOnly).Status == FuelAdviceStatus.Wait, "Date-only before PH midnight");
        Check(Advice(start.AddTicks(-1), dateOnly).ValidUntilUtc == start, "Date-only pre-day advice ends at midnight");
        Check(Advice(start, dateOnly).Reason == "effective_time_unknown", "Date-only at PH midnight");
        Check(Advice(start.AddDays(1).AddTicks(-1), dateOnly).Status == FuelAdviceStatus.Unknown, "Date-only during PH date");
        Check(Advice(start.AddDays(1), dateOnly).Status == FuelAdviceStatus.GasNow, "Date-only after PH date");
        Check(Advice(effective, rollback, dateOnly).Status == FuelAdviceStatus.Unknown, "Missing timing is not guessed");
        var next = increase with { EffectiveDatePhilippines = date.AddDays(2), EffectiveAtUtc = effective.AddDays(2) };
        Check(Advice(effective, rollback, next).Reason == "increase_upcoming", "Upcoming event replaces prior event");
        var later = next with { EffectiveDatePhilippines = date.AddDays(3), EffectiveAtUtc = effective.AddDays(3),
            MinChangePerLiter = -2, MaxChangePerLiter = -2 };
        Check(Advice(effective, rollback, next, later).Reason == "increase_upcoming", "Nearest upcoming event wins");
        Check(Advice(effective.AddDays(3), rollback, next, later).Reason == "rollback_effective", "Latest applied event wins");
        Check(Advice(effective.AddDays(3), rollback, next with { ValidUntilUtc = effective.AddDays(3) }).Status == FuelAdviceStatus.Unknown,
            "Expired newer event must not revive an older rollback");
        Check(Advice(effective, rollback, forecast with { EffectiveDatePhilippines = date.AddDays(1), EffectiveAtUtc = effective.AddDays(1) })
            .Status == FuelAdviceStatus.GasNow, "Forecast context does not override confirmed advice");
        var json = JsonSerializer.Serialize(applied, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Check(JsonDocument.Parse(json).RootElement.GetProperty("status").GetString() == "gas_now", "Stable snake-case JSON enum");
        Check(RouteRegistry.Public.Any(r => r.Route == ApiRoutes.FuelAdvice && r.Method == "GET"), "Route registry");
        Console.WriteLine("PASS fuel advice timing, expiry, forecasts, conflicts, event priority, JSON and route checks");
    }
}
