using System.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;


var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var adminApiKey = builder.Configuration["ADMIN_API_KEY"];

// In-memory state (resets when you restart the app)
var state = new ServerState
{
    ServerName = "API Gateway 1",
    Status = "stopped",
    Version = "1.0.0"
};

if (string.IsNullOrWhiteSpace(adminApiKey))
{
    throw new InvalidOperationException("ADMIN_API_KEY is not configured");
}

var InstanceId = GetInstanceId();

// Middleware
app.Use(async (context, next) =>
{
    await AdminAuthMiddleware.InvokeAsync(
        context,
        next,
        adminApiKey!,
        InstanceId
    );
});

app.UseMiddleware<RateLimitMiddleware>();

app.MapGet("/ping", () => new ApiResponse<object>());

app.MapGet("/status", () => ApiResults.Ok(state, "success", InstanceId));

app.MapGet("/info", () => ApiResults.Ok(ApiMetadata.Info));

app.MapPost("/start", (HttpRequest request) =>
{
    if(state.Status == "start")
        return ApiResults.BadRequest("Server already running...", InstanceId, "Current status: running");
    
    state.Status = "start";
    return ApiResults.Ok(state, "server is running...", InstanceId);
});

app.MapPost("/stop", (HttpRequest request) =>
{
    if(state.Status == "stop")
        return ApiResults.BadRequest("Server already stopped...", InstanceId);
    
    state.Status = "stop";
    return ApiResults.Ok(state, "server is stopped...", InstanceId);
});

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