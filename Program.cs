var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// In-memory state (resets when you restart the app)
var state = new ServerState
{
    ServerName = "API Gateway 1",
    Status = "stopped",
    Version = "1.0.0"
};

app.MapGet("/status", () => Results.Ok(state));

app.MapPost("/start", () =>
{
    state.Status = "running";
    return Results.Ok(state);
});

app.MapPost("/stop", () =>
{
    state.Status = "stopped";
    return Results.Ok(state);
});

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Add($"http://0.0.0.0:{port}");

app.Run();

public class ServerState
{
    public string ServerName { get; set; } = "";
    public string Status { get; set; } = "";
    public string Version { get; set; } = "";
}