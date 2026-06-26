using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;


Console.Error.WriteLine("startup: entering program");
var builder = WebApplication.CreateBuilder(args);
Console.Error.WriteLine("startup: builder created");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(DbConfig.ResolveConnectionString(builder.Configuration)));

Console.Error.WriteLine("startup: building app");
var app = builder.Build();
Console.Error.WriteLine("startup: app built");

// Apply any pending migrations on startup so the schema exists locally and on Render.
Console.Error.WriteLine("startup: migrating database");
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
Console.Error.WriteLine("startup: database migrated");

// Behind Render's proxy the real client IP is in X-Forwarded-For; surface it as RemoteIpAddress.
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedOptions.KnownIPNetworks.Clear();
forwardedOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedOptions);

// In-memory state (resets when you restart the app)
var state = new ServerState
{
    ServerName = "AEnlight API Server",
    Status = ServerStatus.Stopped,
};

var health = new HealthState
{
    ServerName = "AEnlight API Server",
    Status = "healthy",
};

var InstanceId = GetInstanceId();

IResult GetServerStatus() => ApiResults.Ok(state, "success", InstanceId);

IResult StartServer()
{
    if (state.Status == ServerStatus.Running)
        return ApiResults.BadRequest("Server already running...", InstanceId, "Current status: running");

    var now = DateTime.UtcNow;
    state.Status = ServerStatus.Running;
    state.StartedAt = now;
    state.LastStartedAt = now;
    return ApiResults.Ok(state, "server is running...", InstanceId);
}

IResult StopServer()
{
    if (state.Status == ServerStatus.Stopped)
        return ApiResults.BadRequest("Server already stopped...", InstanceId);

    var now = DateTime.UtcNow;
    if (state.StartedAt is not null)
        state.AccumulatedUptimeSeconds += (now - state.StartedAt.Value).TotalSeconds;

    state.Status = ServerStatus.Stopped;
    state.StartedAt = null;
    state.LastStoppedAt = now;
    return ApiResults.Ok(state, "server is stopped...", InstanceId);
}

IResult RestartServer()
{
    if (state.Status != ServerStatus.Running)
        return ApiResults.BadRequest("Server is not running...", InstanceId, "Start the server before restarting.");

    var now = DateTime.UtcNow;
    if (state.StartedAt is not null)
        state.AccumulatedUptimeSeconds += (now - state.StartedAt.Value).TotalSeconds;

    state.StartedAt = now;
    state.LastStartedAt = now;
    state.RestartCount++;
    return ApiResults.Ok(state, "server restarted...", InstanceId);
}

var auth = new AuthService(app.Configuration);

// Middleware
app.UseMiddleware<RateLimitMiddleware>();
app.UseMiddleware<AdminAuthMiddleware>();

app.MapGet(ApiRoutes.Ping, () => new ApiResponse<object>());
app.MapGet(ApiRoutes.Health, () => ApiResults.Ok(health, "success", InstanceId));
app.MapGet(ApiRoutes.Status, GetServerStatus);
app.MapGet(ApiRoutes.Info, () => ApiResults.Ok(ApiMetadata.Info));
app.MapGet(ApiRoutes.Routes, () => ApiResults.Ok(RouteRegistry.Public, "public_routes", InstanceId));

app.MapAuthEndpoints(auth, InstanceId);
app.MapPlayerDataEndpoints(InstanceId);

app.MapGet(ApiRoutes.AdminRoutes, () => ApiResults.Ok(RouteRegistry.Admin, "admin_routes", InstanceId));
app.MapGet(ApiRoutes.AdminServerStatus, GetServerStatus);
app.MapPost(ApiRoutes.AdminServerStart, StartServer);
app.MapPost(ApiRoutes.AdminServerStop, StopServer);
app.MapPost(ApiRoutes.AdminServerRestart, RestartServer);

// Compatibility aliases for older admin tools. Prefer /admin/server/* in new clients.
app.MapPost(ApiRoutes.LegacyAdminStart, StartServer);
app.MapPost(ApiRoutes.LegacyAdminStop, StopServer);
app.MapPost(ApiRoutes.LegacyAdminRestart, RestartServer);

app.MapFallback((HttpContext context) => ApiResults.NotFound("The requested endpoint does not exist.", InstanceId, context.Request.Path));


var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Add($"http://0.0.0.0:{port}");

Console.Error.WriteLine($"startup: running on {port}");
app.Run();


static string GetInstanceId()
{
    // On Render: unique per running instance
    var renderInstanceId = Environment.GetEnvironmentVariable("RENDER_INSTANCE_ID");
    if (!string.IsNullOrWhiteSpace(renderInstanceId))
        return renderInstanceId;

    // Local fallback
    return Environment.MachineName;
}
