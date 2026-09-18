using System.Collections.Generic;

public static class APIConstants
{
    public const string BaseUrl = "https://gaspricebackend.onrender.com";
    public const int TimeoutSeconds = 30;

    public static class Headers
    {
        public const string Accept = "Accept";
        public const string ContentType = "Content-Type";
        public const string Authorization = "Authorization";
        public const string AppInstanceId = "X-App-Instance-Id";
        public const string GuestCredential = "X-Guest-Credential";
        public const string Json = "application/json";
        public const string BearerPrefix = "Bearer ";
    }

    public static class Endpoints
    {
        // public const string Ping = "/ping";
        public const string Health = "/health";
        public const string Status = "/status";
        public const string Info = "/info";
        public const string Routes = "/routes";
        public const string AuthStatus = "/auth/status";
        public const string GuestLogin = "/auth/guest/login";
        public const string Logout = "/auth/logout";
        public const string PlayerData = "/player/data";
        public const string PlayerProfile = "/player/profile";
        public const string AiChat = "/ai/chat";
        public const string FuelPrices = "/ai/fuel-prices";

        public static class Admin
        {
            public const string Routes = "/admin/routes";
            public const string Changelog = "/admin/changelog";
            public const string Status = "/admin/server/status";
            public const string Start = "/admin/server/start";
            public const string Stop = "/admin/server/stop";
            public const string Restart = "/admin/server/restart";
        }
    }

    public static Dictionary<string, string> JsonHeaders() => new()
    {
        [Headers.Accept] = Headers.Json,
        [Headers.ContentType] = Headers.Json
    };
}
