// A guest record, one row per app installation. AppInstanceId identifies the record;
// the credential and access-token hashes prove ownership and authorize a session.
public class Guest
{
    public string AppInstanceId { get; set; } = "";   // primary key
    public string? GuestCredentialHash { get; set; }
    public string? AccessTokenHash { get; set; }
    public DateTime? AccessTokenExpiresAt { get; set; }

    // Transitional support for accounts created before hashed credentials.
    // Cleared as soon as the account completes its first upgraded login.
    public string? LegacyToken { get; set; }

    public long PlayerId { get; set; }           // display-safe numeric id
    public string PlayerName { get; set; } = "";

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? DeviceType { get; set; }       // best-effort: iPhone / Android / Windows / Browser / ...

    public DateTime CreatedAt { get; set; }
    public DateTime LastLoginAt { get; set; }
    public int LoginCount { get; set; }
    public bool IsLoggedIn { get; set; }

    public DateTime? LastLogoutAt { get; set; }
    public int LogoutCount { get; set; }
}
