using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

static class DoeWorkerIdleChecks
{
    public static async Task RunAsync()
    {
        // Exercise the real hosted loop with empty query results and no database/network access.
        var queries = new EmptyJobs();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DoeImport:ScheduleEnabled"] = "false" }).Build());
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(
            "Host=127.0.0.1;Port=1;Database=never_connect;Username=test")
            .AddInterceptors(new NoConnection(), queries));
        services.AddSingleton<DoeImportWorker>();
        await using var provider = services.BuildServiceProvider();
        using var worker = provider.GetRequiredService<DoeImportWorker>();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForQueries(queries, 2);
            worker.Wake();
            await WaitForQueries(queries, 4);
            await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(worker.Wake)));
            // Let the coalesced signals drain, then exceed the former one-minute poll interval.
            await Task.Delay(250);
            var idleCount = queries.Count;
            Console.WriteLine("Checking DOE worker stays asleep beyond the former one-minute poll interval...");
            await Task.Delay(TimeSpan.FromSeconds(62));
            if (queries.Count != idleCount)
                throw new Exception("An empty DOE worker must not query the database while idle.");
            worker.Wake();
            await WaitForQueries(queries, idleCount + 2);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(stop.Token);
        }
        Console.WriteLine("Passed DOE startup, wake, concurrent signal, idle and shutdown checks.");
    }

    private static async Task WaitForQueries(EmptyJobs queries, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (queries.Count < count) await Task.Delay(10, timeout.Token);
        await Task.Delay(50, timeout.Token);
    }

    private sealed class NoConnection : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class EmptyJobs : DbCommandInterceptor
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var table = new DataTable();
            var entity = eventData.Context!.Model.FindEntityType(typeof(DoeImportJob))!;
            foreach (var property in entity.GetProperties())
                table.Columns.Add(property.GetColumnName(), Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType);
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
        }
    }
}
