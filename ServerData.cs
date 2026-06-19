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

public class ServerState
{
    public string ServerName { get; set; } = "";
    public string Status { get; set; } = "";
    public string Version { get; set; } = "";

    // UTC timestamp of when the server was last started; null while stopped.
    public DateTime? StartedAt { get; set; }

    // How long the server has been active since StartedAt; null while stopped.
    public double? UptimeSeconds =>
        StartedAt is null ? null : (DateTime.UtcNow - StartedAt.Value).TotalSeconds;

    public string? Uptime =>
        StartedAt is null ? null : FormatUptime(DateTime.UtcNow - StartedAt.Value);

    private static string FormatUptime(TimeSpan t) =>
        $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m {t.Seconds}s";
}

public record ApiInfo 
{
    public string DeveloperName {get; init; } = "";
    public string ContactEmail {get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public string CopyrightNotice {get; init; } = "";
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
    };
}

public record RouteInfo(string Route, string Method);