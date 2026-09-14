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

public sealed class PingResponse : ApiResponse<object>
{
    public string ProcessingTime { get; init; } = "0.00ms";
}

public class ApiError
{
    public string Error {get; set;} = "";
    public string? Detail {get; set;} = "";

}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TokenType
{
    Guest,
    Email,
    Gmail,
    AppleId
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

public static class ApiRoutes
{
    public const string Ping = "/ping";
    public const string Health = "/health";
    public const string Status = "/status";
    public const string Info = "/info";
    public const string Routes = "/routes";

    public const string AuthStatus = "/auth/status";
    public const string AuthGuestLogin = "/auth/guest/login";
    public const string AuthLogout = "/auth/logout";

    public const string PlayerData = "/player/data";
    public const string PlayerProfile = "/player/profile";
    public const string AiChat = "/ai/chat";
    public const string AiFuelPrices = "/ai/fuel-prices";

    public const string AdminServerStatus = "/admin/server/status";
    public const string AdminServerStart = "/admin/server/start";
    public const string AdminServerStop = "/admin/server/stop";
    public const string AdminServerRestart = "/admin/server/restart";
    public const string AdminRoutes = "/admin/routes";
    public const string AdminChangelog = "/admin/changelog";

    public const string LegacyAdminStart = "/admin/start";
    public const string LegacyAdminStop = "/admin/stop";
    public const string LegacyAdminRestart = "/admin/restart";
}

public static class RouteRegistry
{
    public static readonly RouteInfo[] Public =
    {
        new(ApiRoutes.Ping, "GET"),
        new(ApiRoutes.Health, "GET"),
        new(ApiRoutes.Status, "GET"),
        new(ApiRoutes.Info, "GET"),
        new(ApiRoutes.Routes, "GET"),
        new(ApiRoutes.AuthStatus, "GET"),
        new(ApiRoutes.AuthGuestLogin, "POST"),
        new(ApiRoutes.AuthLogout, "POST"),
        new(ApiRoutes.PlayerData, "GET"),
        new(ApiRoutes.PlayerData, "PATCH"),
        new(ApiRoutes.PlayerProfile, "PATCH"),
        new(ApiRoutes.AiChat, "POST"),
        new(ApiRoutes.AiFuelPrices, "POST"),
    };

    public static readonly RouteInfo[] Admin =
    {
        new(ApiRoutes.AdminRoutes, "GET"),
        new(ApiRoutes.AdminChangelog, "GET"),
        new(ApiRoutes.AdminServerStatus, "GET"),
        new(ApiRoutes.AdminServerStart, "POST"),
        new(ApiRoutes.AdminServerStop, "POST"),
        new(ApiRoutes.AdminServerRestart, "POST"),
        new(ApiRoutes.LegacyAdminStart, "POST", true),
        new(ApiRoutes.LegacyAdminStop, "POST", true),
        new(ApiRoutes.LegacyAdminRestart, "POST", true),
    };
}

public record RouteInfo(string Route, string Method, bool IsLegacy = false);

public static class APIVersion
{
    public const string Version = "1.9.4";
}
