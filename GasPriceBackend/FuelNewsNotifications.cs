using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

public sealed class FuelNewsSubscription
{
    public string AppInstanceId { get; set; } = "";
    public string PushToken { get; set; } = "";
    public bool Enabled { get; set; }
    public bool IncludeForecasts { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class FuelNewsDelivery
{
    public Guid Id { get; set; }
    public Guid NewsId { get; set; }
    public int Revision { get; set; }
    public string AppInstanceId { get; set; } = "";
    public string Status { get; set; } = "queued";
    public int Attempts { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public string? TicketId { get; set; }
    public string? Error { get; set; }
}

public sealed record FuelNewsSubscriptionRequest(string PushToken, bool IncludeForecasts);
public sealed record FuelNewsSubscriptionView(bool Enabled, bool IncludeForecasts, bool Available);

public static partial class FuelNewsSubscriptionEndpoints
{
    [GeneratedRegex(@"^(ExpoPushToken|ExponentPushToken)\[[A-Za-z0-9_-]{10,200}\]$")]
    private static partial Regex TokenPattern();

    public static void MapFuelNewsSubscriptionEndpoints(this WebApplication app, AuthService auth, string instanceId)
    {
        app.MapGet(ApiRoutes.FuelNewsSubscription, async (HttpRequest request, AppDbContext db, IConfiguration configuration) =>
        {
            var player = await PlayerAuthentication.AuthenticateAsync(request, db, auth);
            if (!player.IsValid) return Failure(player, instanceId);
            var subscription = await db.FuelNewsSubscriptions.AsNoTracking().SingleOrDefaultAsync(s => s.AppInstanceId == player.Guest!.AppInstanceId);
            return ApiResults.Ok(new FuelNewsSubscriptionView(subscription?.Enabled ?? false, subscription?.IncludeForecasts ?? false,
                configuration.GetValue<bool>("FuelNews:PushEnabled")), "fuel_news_subscription", instanceId);
        });
        app.MapPut(ApiRoutes.FuelNewsSubscription, async (HttpRequest request, AppDbContext db, TimeProvider clock, IConfiguration configuration, CancellationToken ct) =>
        {
            var player = await PlayerAuthentication.AuthenticateAsync(request, db, auth);
            if (!player.IsValid) return Failure(player, instanceId);
            if (!configuration.GetValue<bool>("FuelNews:PushEnabled"))
                return ApiResults.ServiceUnavailable("fuel_news_push_not_enabled", instanceId);
            if (request.Query.Count != 0) return ApiResults.BadRequest("This endpoint takes no query parameters.", instanceId);
            FuelNewsSubscriptionRequest body;
            try { body = await FuelNewsEndpoints.ReadJsonAsync<FuelNewsSubscriptionRequest>(request, ct); }
            catch (InvalidDataException ex) { return ApiResults.BadRequest(ex.Message, instanceId); }
            if (body.PushToken is null || !TokenPattern().IsMatch(body.PushToken))
                return ApiResults.BadRequest("Provide a valid Expo push token.", instanceId);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(746302192)", ct);
            var owner = player.Guest!.AppInstanceId;
            if (await db.FuelNewsSubscriptions.AnyAsync(s => s.PushToken == body.PushToken && s.AppInstanceId != owner, ct))
                return ApiResults.BadRequest("The device is registered to another guest identity.", instanceId);
            var subscription = await db.FuelNewsSubscriptions.SingleOrDefaultAsync(s => s.AppInstanceId == owner, ct);
            if (subscription is null) { subscription = new() { AppInstanceId = owner }; db.FuelNewsSubscriptions.Add(subscription); }
            subscription.PushToken = body.PushToken;
            subscription.Enabled = true;
            subscription.IncludeForecasts = body.IncludeForecasts;
            subscription.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return ApiResults.Ok(new FuelNewsSubscriptionView(true, subscription.IncludeForecasts, true), "fuel_news_subscription", instanceId);
        });
        app.MapDelete(ApiRoutes.FuelNewsSubscription, async (HttpRequest request, AppDbContext db, TimeProvider clock, IConfiguration configuration, CancellationToken ct) =>
        {
            var player = await PlayerAuthentication.AuthenticateAsync(request, db, auth);
            if (!player.IsValid) return Failure(player, instanceId);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(746302192)", ct);
            var subscription = await db.FuelNewsSubscriptions.SingleOrDefaultAsync(s => s.AppInstanceId == player.Guest!.AppInstanceId, ct);
            if (subscription is not null) { subscription.Enabled = false; subscription.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime; }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return ApiResults.Ok(new FuelNewsSubscriptionView(false, subscription?.IncludeForecasts ?? false,
                configuration.GetValue<bool>("FuelNews:PushEnabled")), "fuel_news_subscription", instanceId);
        });
    }

    private static IResult Failure(PlayerAuthResult player, string id) => player.IsBadRequest
        ? ApiResults.BadRequest(player.Error, id) : ApiResults.Unauthorized(player.Error, id);
}

// A persistent outbox retries transient failures and polls Expo receipts. Delivery is at least once:
// a host crash after Expo accepts a send but before SaveChanges may repeat a notification.
public sealed class FuelNewsNotificationWorker(IServiceScopeFactory scopes, IHttpClientFactory clients,
    IConfiguration configuration, TimeProvider clock, ILogger<FuelNewsNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("FuelNews:PushEnabled")) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Fuel news push processing failed ({Type}).", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task ProcessAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // A session lock excludes other workers without holding saved tickets in a long transaction.
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(746302193)";
        if (!Equals(await command.ExecuteScalarAsync(ct), true)) return;
        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var deliveries = await db.FuelNewsDeliveries.Where(d => (d.Status == "queued" || d.Status == "ticketed") &&
                d.NextAttemptAtUtc <= now).OrderBy(d => d.NextAttemptAtUtc).Take(50).ToListAsync(ct);
            var client = clients.CreateClient("fuel-news-push");
            foreach (var delivery in deliveries)
            {
                var subscription = await db.FuelNewsSubscriptions.FindAsync([delivery.AppInstanceId], ct);
                var news = await db.FuelNews.FindAsync([delivery.NewsId], ct);
                if (subscription is null || !subscription.Enabled || news is null || news.ExpiresAtUtc <= now ||
                    news.SupersededById is not null || (news.Status == "forecast" && !subscription.IncludeForecasts))
                { delivery.Status = "cancelled"; continue; }
                // A later material update makes older unsent revisions obsolete.
                if (delivery.Status == "queued" && await db.FuelNewsDeliveries.AnyAsync(d => d.NewsId == delivery.NewsId &&
                    d.AppInstanceId == delivery.AppInstanceId && d.Revision > delivery.Revision, ct))
                { delivery.Status = "cancelled"; continue; }
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Status == "ticketed" ? "getReceipts" : "send");
                    if (configuration["EXPO_ACCESS_TOKEN"] is { Length: > 0 } accessToken)
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    if (delivery.Status == "ticketed") request.Content = JsonContent.Create(new { ids = new[] { delivery.TicketId } });
                    else
                    {
                        var revision = await db.FuelNewsRevisions.FindAsync([news.Id, delivery.Revision], ct);
                        var article = JsonSerializer.Deserialize<FuelNewsContent>(revision!.ContentJson, FuelNewsValidation.Json)!;
                        request.Content = JsonContent.Create(new { to = subscription.PushToken, title = article.Title,
                            body = article.Summary.Length > 180 ? article.Summary[..177] + "…" : article.Summary,
                            data = new { newsId = news.Id, revision = delivery.Revision }, channelId = "fuel-news", sound = "default", ttl = 3600 });
                    }
                    using var response = await client.SendAsync(request, ct);
                    delivery.Attempts++;
                    if (!response.IsSuccessStatusCode)
                    {
                        Retry(delivery, now, "expo_http_" + (int)response.StatusCode,
                            response.StatusCode == System.Net.HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500);
                        continue;
                    }
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    if (!document.RootElement.TryGetProperty("data", out var data)) { Retry(delivery, now, "invalid_expo_response", true); continue; }
                    if (delivery.Status == "ticketed")
                    {
                        if (!data.TryGetProperty(delivery.TicketId!, out data))
                        {
                            // Receipts may take up to 30 minutes to become available.
                            Retry(delivery, now, "receipt_pending", true); continue;
                        }
                    }
                    else if (data.ValueKind == JsonValueKind.Array) data = data[0];
                    if (data.GetProperty("status").GetString() == "ok")
                    {
                        delivery.Error = null;
                        if (delivery.Status == "ticketed") delivery.Status = "accepted";
                        else
                        {
                            delivery.TicketId = data.GetProperty("id").GetString(); delivery.Status = "ticketed";
                            delivery.Attempts = 0; delivery.NextAttemptAtUtc = now.AddMinutes(15);
                        }
                    }
                    else
                    {
                        var code = data.TryGetProperty("details", out var details) && details.TryGetProperty("error", out var error)
                            ? error.GetString() : "expo_error";
                        if (code == "DeviceNotRegistered")
                        {
                            // An old receipt must not disable a token registered after this article was queued.
                            await db.FuelNewsSubscriptions.Where(s => s.AppInstanceId == delivery.AppInstanceId &&
                                s.PushToken == subscription.PushToken && s.UpdatedAtUtc <= delivery.CreatedAtUtc)
                                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false), ct);
                            await db.Entry(subscription).ReloadAsync(ct);
                        }
                        Retry(delivery, now, code ?? "expo_error", code == "MessageRateExceeded");
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException ||
                                            (ex is OperationCanceledException && !ct.IsCancellationRequested))
                { delivery.Attempts++; Retry(delivery, now, "expo_unavailable", true); }
                finally
                {
                    // Persist each ticket before processing another device.
                    await db.SaveChangesAsync(ct);
                }
            }
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            command.CommandText = "SELECT pg_advisory_unlock(746302193)";
            await command.ExecuteScalarAsync(CancellationToken.None);
            await db.Database.CloseConnectionAsync();
        }
    }

    private static void Retry(FuelNewsDelivery delivery, DateTime now, string error, bool transient)
    {
        delivery.Error = error;
        if (!transient || delivery.Attempts >= 8) delivery.Status = "failed";
        else delivery.NextAttemptAtUtc = now.AddMinutes(Math.Min(30, Math.Pow(2, delivery.Attempts)));
    }
}
