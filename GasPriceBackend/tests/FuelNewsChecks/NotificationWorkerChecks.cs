using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

static class NotificationWorkerChecks
{
    public static async Task RunAsync(string connection, FuelNewsContent template, string owner)
    {
        var clock = new ManualClock(DateTime.UtcNow);
        var signal = new FuelNewsNotificationSignal();
        var queries = new Queries();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection).AddInterceptors(queries));
        await using var provider = services.BuildServiceProvider();
        var clients = new Push();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["FuelNews:PushEnabled"] = "true" }).Build();
        FuelNewsNotificationWorker Worker(FuelNewsNotificationSignal wake, IConfiguration? settings = null) => new(
            provider.GetRequiredService<IServiceScopeFactory>(), clients, settings ?? config, clock,
            NullLogger<FuelNewsNotificationWorker>.Instance, wake);
        async Task<FuelNewsImportResult> Publish(FuelNewsNotificationSignal? wake = null)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var key = "worker-" + Guid.NewGuid().ToString("N");
            return await new FuelNewsService(db, clock, wake ?? signal).ImportAsync(new(template with
            {
                ImportKey = key, TopicKey = key, ExpiresAtUtc = clock.GetUtcNow().UtcDateTime.AddDays(2)
            }, null), default);
        }
        async Task<FuelNewsDelivery> Delivery(Guid newsId)
        {
            using var scope = provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().FuelNewsDeliveries
                .AsNoTracking().SingleAsync(d => d.NewsId == newsId);
        }
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.FuelNewsDeliveries.ExecuteDeleteAsync();
            var subscription = await db.FuelNewsSubscriptions.SingleAsync(s => s.AppInstanceId == owner);
            subscription.Enabled = true;
            subscription.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
        }

        await CheckPublicationSignals(connection, template, clock);

        // No signal survives the simulated restart: the new worker must discover this row itself.
        var startup = await Publish(new FuelNewsNotificationSignal());
        using var worker = Worker(signal);
        await worker.StartAsync(default);
        try
        {
            await Until(async () => (await Delivery(startup.Item.Id)).Status == "ticketed");
            var ticket = await Delivery(startup.Item.Id);
            Check(ticket.NextAttemptAtUtc == clock.GetUtcNow().UtcDateTime.AddMinutes(15), "Receipt deadline changed.");
            await Settle();
            var count = queries.Count;
            clock.Advance(TimeSpan.FromMinutes(14));
            await Settle();
            Check(queries.Count == count && clients.Receipts == 0, "Receipt queried before its deadline.");
            clients.PendingReceipt = true;
            clock.Advance(TimeSpan.FromMinutes(1));
            await Until(async () => (await Delivery(startup.Item.Id)).Error == "receipt_pending");
            ticket = await Delivery(startup.Item.Id);
            Check(ticket.NextAttemptAtUtc == clock.GetUtcNow().UtcDateTime.AddMinutes(2), "Receipt retry deadline changed.");
            clients.PendingReceipt = false;
            clock.Advance(TimeSpan.FromMinutes(2));
            await Until(async () => (await Delivery(startup.Item.Id)).Status == "accepted");
            await Settle();
            count = queries.Count;
            clock.Advance(TimeSpan.FromHours(3));
            await Settle();
            Check(queries.Count == count, "Empty outbox queried while idle.");
            Check(clients.Sends == 1 && clients.Receipts == 2, "Receipt check resent a push.");
            Console.WriteLine("PASS startup recovery, exact receipt deadlines, pending receipts and zero idle polling");

            clients.FailNext = true;
            var retry = await Publish();
            await Until(async () => (await Delivery(retry.Item.Id)).Error == "expo_http_503");
            var failed = await Delivery(retry.Item.Id);
            Check(failed.NextAttemptAtUtc == clock.GetUtcNow().UtcDateTime.AddMinutes(2), "Send retry deadline changed.");
            var earlier = await Publish();
            await Until(async () => (await Delivery(earlier.Item.Id)).Status == "ticketed");
            await Settle();
            count = queries.Count;
            clock.Advance(TimeSpan.FromMinutes(1));
            await Settle();
            Check(queries.Count == count, "Retry queried before NextAttemptAtUtc.");
            clock.Advance(TimeSpan.FromMinutes(1));
            await Until(async () => (await Delivery(retry.Item.Id)).Status == "ticketed");
            clock.Advance(TimeSpan.FromMinutes(15));
            await Until(async () => (await Delivery(retry.Item.Id)).Status == "accepted");
            Console.WriteLine("PASS committed publication wakes sleeping worker; transient retry respects NextAttemptAtUtc");

            // Hold an Expo call while another host wakes and contends for the same session lock.
            clients.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocked = await Publish();
            await Until(() => Task.FromResult(clients.InFlight));
            var secondSignal = new FuelNewsNotificationSignal();
            using var secondWorker = Worker(secondSignal);
            await secondWorker.StartAsync(default);
            try
            {
                await Settle();
                var more = await Task.WhenAll(Enumerable.Range(0, 55).Select(_ => Publish(secondSignal)));
                await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(signal.Wake)));
                var sends = clients.Sends;
                clock.Advance(TimeSpan.FromSeconds(29));
                await Settle();
                Check(clients.Sends == sends, "Competing worker sent while advisory lock was held.");
                clients.Gate.SetResult();
                await Until(async () =>
                {
                    using var scope = provider.CreateScope();
                    return await scope.ServiceProvider.GetRequiredService<AppDbContext>().FuelNewsDeliveries
                        .CountAsync(d => d.Status == "ticketed") == more.Length + 1;
                });
                // More than one 50-row batch must drain without advancing time or external wakes.
                clock.Advance(TimeSpan.FromSeconds(1));
                await Settle();
                Check(clients.MaxInFlight == 1, "Multiple workers sent concurrently.");
                clock.Advance(TimeSpan.FromMinutes(15));
                await Until(async () =>
                {
                    using var scope = provider.CreateScope();
                    return !await scope.ServiceProvider.GetRequiredService<AppDbContext>().FuelNewsDeliveries
                        .AnyAsync(d => d.Status == "queued" || d.Status == "ticketed");
                });
                // A peer may retain one deadline from before another host drained the outbox.
                // Let it reconcile that known pending work, then verify it stays asleep.
                clock.Advance(TimeSpan.FromHours(1));
                await Settle();
                count = queries.Count;
                clock.Advance(TimeSpan.FromHours(1));
                await Settle();
                Check(queries.Count == count, "Workers continued polling after concurrent work drained.");
                Console.WriteLine("PASS concurrent committed publications/wakes, advisory-lock exclusion and batches over 50");
            }
            finally { clients.Gate.TrySetResult(); await secondWorker.StopAsync(default); }
        }
        finally { await worker.StopAsync(default); }

        // A crashed lock holder must not strand locally signaled pending work on a competing host.
        var pending = await Publish(new FuelNewsNotificationSignal());
        using var holder = new Npgsql.NpgsqlConnection(connection);
        await holder.OpenAsync();
        await using (var command = new Npgsql.NpgsqlCommand("SELECT pg_advisory_lock(746302193)", holder))
            await command.ExecuteNonQueryAsync();
        using var recovering = Worker(new FuelNewsNotificationSignal());
        await recovering.StartAsync(default);
        try
        {
            await Settle();
            await using (var command = new Npgsql.NpgsqlCommand("SELECT pg_advisory_unlock(746302193)", holder))
                await command.ExecuteNonQueryAsync();
            await holder.CloseAsync();
            clock.Advance(TimeSpan.FromSeconds(30));
            await Until(async () => (await Delivery(pending.Item.Id)).Status == "ticketed");
            Console.WriteLine("PASS pending work retries after a competing server releases its lock");
        }
        finally { await recovering.StopAsync(default); }

        using (var receiptsAfterRestart = Worker(new FuelNewsNotificationSignal()))
        {
            await receiptsAfterRestart.StartAsync(default);
            try
            {
                await Settle();
                var count = queries.Count;
                var receipts = clients.Receipts;
                clock.Advance(TimeSpan.FromMinutes(14));
                await Settle();
                Check(queries.Count == count && clients.Receipts == receipts, "Restart polled a future receipt early.");
                clock.Advance(TimeSpan.FromMinutes(1));
                await Until(async () => (await Delivery(pending.Item.Id)).Status == "accepted");
            }
            finally { await receiptsAfterRestart.StopAsync(default); }
        }
        Console.WriteLine("PASS startup recovery of persisted receipt deadlines without an in-memory wake");

        var disabledQueries = queries.Count;
        var disabledSignal = new FuelNewsNotificationSignal();
        using var disabled = Worker(disabledSignal, new ConfigurationBuilder().Build());
        await disabled.StartAsync(default);
        disabledSignal.Wake();
        clock.Advance(TimeSpan.FromDays(1));
        await Settle();
        Check(queries.Count == disabledQueries, "PushEnabled=false performed database work.");
        await disabled.StopAsync(default);
        Console.WriteLine("PASS PushEnabled=false and shutdown cancellation");
    }

    private static async Task CheckPublicationSignals(string connection, FuelNewsContent template, TimeProvider clock)
    {
        var signal = new FuelNewsNotificationSignal();
        var key = "signal-" + Guid.NewGuid().ToString("N");
        var request = new FuelNewsImport(template with { ImportKey = key, TopicKey = key }, null);
        using var stop = new CancellationTokenSource();
        var wake = signal.WaitAsync(null, clock, stop.Token);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection);
        await using (var rejected = new AppDbContext(options.AddInterceptors(new RejectSave()).Options))
        {
            try
            {
                await new FuelNewsService(rejected, clock, signal).ImportAsync(request, default);
                throw new Exception("Injected save failure did not occur.");
            }
            catch (InvalidOperationException ex) when (ex.Message == "fixture save failure") { }
        }
        Check(!wake.IsCompleted, "Failed save woke the worker.");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        Check(!await db.FuelNews.AnyAsync(n => n.ImportKey == key), "Rejected publication partially committed.");
        var service = new FuelNewsService(db, clock, signal);
        var saved = await service.ImportAsync(request, default);
        await wake.WaitAsync(TimeSpan.FromSeconds(5));
        await using (var visible = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options))
            Check(await visible.FuelNewsDeliveries.AnyAsync(d => d.NewsId == saved.Item.Id), "Wake preceded committed delivery visibility.");
        var idle = signal.WaitAsync(null, clock, stop.Token);
        await service.ImportAsync(request, default);
        Check(!idle.IsCompleted, "Idempotent publication woke an idle worker without adding deliveries.");
        stop.Cancel();
        try { await idle; } catch (OperationCanceledException) { }
        await db.FuelNewsDeliveries.Where(d => d.NewsId == saved.Item.Id).ExecuteDeleteAsync();
        Console.WriteLine("PASS signal after commit, no signal on failed save/unchanged import, atomic outbox persistence");
    }

    private sealed class RejectSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken ct = default)
            => throw new InvalidOperationException("fixture save failure");
    }

    private static async Task Until(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await predicate()) await Task.Delay(10, timeout.Token);
    }
    private static Task Settle() => Task.Delay(150);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    private sealed class Queries : DbCommandInterceptor
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { Interlocked.Increment(ref _count); return ValueTask.FromResult(result); }
    }
    private sealed class Push : HttpMessageHandler, IHttpClientFactory
    {
        public int Sends, Receipts, MaxInFlight;
        private int _inFlight;
        public bool InFlight => Volatile.Read(ref _inFlight) > 0;
        public bool FailNext, PendingReceipt;
        public TaskCompletionSource? Gate;
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://push.example.test/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host != "push.example.test") throw new Exception("Unexpected push host.");
            var active = Interlocked.Increment(ref _inFlight);
            MaxInFlight = Math.Max(MaxInFlight, active);
            try
            {
                if (request.RequestUri.AbsolutePath.EndsWith("send"))
                {
                    Interlocked.Increment(ref Sends);
                    if (Gate is { } gate) await gate.Task.WaitAsync(ct);
                    if (FailNext) { FailNext = false; return new(HttpStatusCode.ServiceUnavailable); }
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = new { status = "ok", id = "worker-ticket" } }) };
                }
                Interlocked.Increment(ref Receipts);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = PendingReceipt
                    ? new Dictionary<string, object>() : new() { ["worker-ticket"] = new { status = "ok" } } }) };
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
    }

    private sealed class ManualClock(DateTime now) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() { lock (_gate) return new(now); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }
        public void Advance(TimeSpan elapsed)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                now += elapsed;
                due = _timers.Where(t => t.Due <= now).ToList();
                foreach (var timer in due) timer.Due = DateTime.MaxValue;
            }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback => callback;
            public object? State => state;
            public DateTime Due = DateTime.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot timers are used.");
                lock (clock._gate) Due = dueTime == Timeout.InfiniteTimeSpan ? DateTime.MaxValue : clock.GetUtcNow().UtcDateTime + dueTime;
                return true;
            }
            public void Dispose() { lock (clock._gate) clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
