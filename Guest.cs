// A guest record, one row per device. The device_id is the stable identity the
// token is derived from; the rest is tracking/analytics captured on login/logout.
public class Guest
{
    public string DeviceId { get; set; } = "";   // primary key
    public string Token { get; set; } = "";

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? DeviceType { get; set; }       // best-effort: iPhone / Android / Windows / Browser / ...

    public DateTime CreatedAt { get; set; }
    public DateTime LastLoginAt { get; set; }
    public int LoginCount { get; set; }

    public DateTime? LastLogoutAt { get; set; }
    public int LogoutCount { get; set; }
}
