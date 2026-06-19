using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.HttpResults;


var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var admin = app.MapGroup("/admin");
string version = "1.2.6";

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