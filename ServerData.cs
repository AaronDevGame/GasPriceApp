using System.Text.Json.Serialization;

public class ApiResponse<T>
{
    public int Code { get ; set;}  = 200;
    public string? Message { get; set; } = "Success";
    public string? InstanceId {get; set; } = ""; 

    public DateTime Date {get; set; } = DateTime.UtcNow;

    public T? Data {get; set;} 
    public ApiError? Error {get; set;}
}

public class ApiError
{
    public string Error {get; set;} = "";
    public string? Detail {get; set;} = "";

}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServerStatus
{
    Stopped,
    Running
}

public class ServerState
{
    public string ServerName { get; set; } = "";
    public ServerStatus Status { get; set; } = ServerStatus.Stopped;
    public string Version { get; set; } = APIVersion.Version;

    // Current session: UTC timestamp of when the server was last started; null while stopped.
    public DateTime? StartedAt { get; set; }

    // Lifecycle history
    public DateTime? LastStartedAt { get; set; }
    public DateTime? LastStoppedAt { get; set; }
    public int RestartCount { get; set; }

    // Cumulative uptime of all completed sessions, in seconds.
    // A field (not a property) so it stays out of the JSON response.
    public double AccumulatedUptimeSeconds;

    // How long the server has been active in the current session; null while stopped.
    public double? UptimeSeconds =>
        StartedAt is null ? null : (DateTime.UtcNow - StartedAt.Value).TotalSeconds;

    public string? Uptime =>
        StartedAt is null ? null : FormatUptime(DateTime.UtcNow - StartedAt.Value);

    // Total uptime across all sessions, including the current one.
    public double TotalUptimeSeconds =>
        AccumulatedUptimeSeconds + (UptimeSeconds ?? 0);

    public string TotalUptime =>
        FormatUptime(TimeSpan.FromSeconds(TotalUptimeSeconds));

    private static string FormatUptime(TimeSpan t) =>
        $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m {t.Seconds}s";
}

public record HealthState
{
    public string ServerName { get; init; } = "";
    public string Status { get; init; } = "healthy";
    public string Version { get; init; } = APIVersion.Version;
}

public record ApiInfo 
{
    public string DeveloperName {get; init; } = "";
    public string ContactEmail {get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public string CopyrightNotice {get; init; } = "";
    public string Version {get; init; } = APIVersion.Version;
}

public static class RouteRegistry
{
    public static readonly RouteInfo[] Public =
    {
        new("/ping", "GET"),
        new("/health", "GET"),
        new("/status", "GET"),
        new("/info", "GET"),
        new("/routes", "GET"),
        new("/login", "POST"),
        new("/logout", "POST"),
    };
}

public record RouteInfo(string Route, string Method);

public static class APIVersion
{
    public const string Version = "1.2.8";
}