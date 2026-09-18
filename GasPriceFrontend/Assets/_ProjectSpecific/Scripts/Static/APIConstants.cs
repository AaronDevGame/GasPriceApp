using System.Collections.Generic;

public static class API
{
    public const string BaseUrl = "https://backendserver-rtqi.onrender.com";

    public static class Endpoints
    {
        public const string Ping = "/ping";
        public const string Health = "/health";
        public const string Status = "/status";

        public static class Admin
        {
            public const string Start = "/admin/start";
            public const string Stop = "/admin/stop";
        }
    }
}



public static class APIHeaders
{
    public static Dictionary<string, string> Json()
    {
        return new Dictionary<string, string>
        {
            { "Content-Type", "application/json" }
        };
    }

    public static Dictionary<string, string> Bearer(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new System.ArgumentException("Bearer token cannot be empty.", nameof(token));
        }

        Dictionary<string, string> headers = Json();
        headers["Authorization"] = $"Bearer {token}";
        return headers;
    }
}
