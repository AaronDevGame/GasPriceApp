using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(DbConfig.ResolveConnectionString(builder.Configuration)));

var app = builder.Build();

// Apply any pending migrations on startup so the schema exists locally and on Render.
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();

// Behind Render's proxy the real client IP is in X-Forwarded-For; surface it as RemoteIpAddress.
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedOptions.KnownIPNetworks.Clear();
forwardedOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedOptions);

var admin = app.MapGroup("/admin");
string version = "1.2.7";

// In-memory state (resets when you restart the app)
var state = new ServerState
{
    ServerName = "AEnlight API Server",
    Status = ServerStatus.Stopped,
    Version = version
};

var health = new HealthState
{
    ServerName = "AEnlight API Server",
    Status = "healthy",
    Version = version
};

var InstanceId = GetInstanceId();

var auth = new AuthService(app.Configuration);

// Middleware

app.UseMiddleware<AdminAuthMiddleware>();
app.UseMiddleware<RateLimitMiddleware>();

app.MapGet("/ping", () => new ApiResponse<object>());

app.MapGet("/health", () => ApiResults.Ok(health, InstanceId));

app.MapGet("/status", () => ApiResults.Ok(state, "success", InstanceId));

app.MapGet("/info", () => ApiResults.Ok(ApiMetadata.Info));

app.MapGet("/routes", () =>ApiResults.Ok(RouteRegistry.Public, "public_routes", InstanceId));

app.MapAuthEndpoints(auth, InstanceId);

app.MapFallback((HttpContext context) => ApiResults.NotFound("The requested endpoint does not exist.", InstanceId, context.Request.Path));

admin.MapPost("/start", (HttpRequest request) =>
{
    if(state.Status == ServerStatus.Running)
        return ApiResults.BadRequest("Server already running...", InstanceId, "Current status: running");

    var now = DateTime.UtcNow;
    state.Status = ServerStatus.Running;
    state.StartedAt = now;
    state.LastStartedAt = now;
    return ApiResults.Ok(state, "server is running...", InstanceId);
});

admin.MapPost("/stop", (HttpRequest request) =>
{
    if(state.Status == ServerStatus.Stopped)
        return ApiResults.BadRequest("Server already stopped...", InstanceId);

    var now = DateTime.UtcNow;
    if (state.StartedAt is not null)
        state.AccumulatedUptimeSeconds += (now - state.StartedAt.Value).TotalSeconds;

    state.Status = ServerStatus.Stopped;
    state.StartedAt = null;
    state.LastStoppedAt = now;
    return ApiResults.Ok(state, "server is stopped...", InstanceId);
});

admin.MapPost("/restart", (HttpRequest request) =>
{
    if(state.Status != ServerStatus.Running)
        return ApiResults.BadRequest("Server is not running...", InstanceId, "Start the server before restarting.");

    var now = DateTime.UtcNow;
    if (state.StartedAt is not null)
        state.AccumulatedUptimeSeconds += (now - state.StartedAt.Value).TotalSeconds;

    state.StartedAt = now;
    state.LastStartedAt = now;
    state.RestartCount++;
    return ApiResults.Ok(state, "server restarted...", InstanceId);
});

admin.MapFallback((HttpContext context) => ApiResults.NotFound("The requested endpoint does not exist.", InstanceId, context.Request.Path));



var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Add($"http://0.0.0.0:{port}");

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