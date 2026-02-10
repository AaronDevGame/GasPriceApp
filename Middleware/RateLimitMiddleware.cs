using Microsoft.AspNetCore.Http;

public class RateLimitMiddleware
{
    private static readonly Dictionary<string, TimeSpan> _cooldowns = new()
    {
        ["/start"]  = TimeSpan.FromSeconds(15),
        ["/stop"]   = TimeSpan.FromSeconds(15),
        ["/ping"]   = TimeSpan.FromSeconds(2),
        ["/status"] = TimeSpan.FromSeconds(5),
        ["/info"]   = TimeSpan.FromSeconds(5),
    };

    // Key = ip + "|" + route
    private static readonly Dictionary<string, DateTime> _lastRequest = new();
    private readonly RequestDelegate _next;

    public RateLimitMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var path = context.Request.Path.Value ?? "";

        if (_cooldowns.TryGetValue(path, out var cooldown))
        {
            var key = $"{ip}|{path}";
            var now = DateTime.UtcNow;

            bool blocked = false;
            int retryAfterSeconds = 0;

            lock (_lastRequest)
            {
                if (_lastRequest.TryGetValue(key, out var lastTime))
                {
                    var elapsed = now - lastTime;
                    if (elapsed < cooldown)
                    {
                        blocked = true;
                        retryAfterSeconds = (int)Math.Ceiling((cooldown - elapsed).TotalSeconds);
                    }
                    else
                    {
                        _lastRequest[key] = now;
                    }
                }
                else
                {
                    _lastRequest[key] = now;
                }
            }

            if (blocked)
            {
                context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                await context.Response.WriteAsJsonAsync(new ApiResponse<object>
                {
                    Code = ErrorCodes.TooManyRequest,
                    Message = "too_many_requests",
                    Error = new ApiError
                    {
                        Error = "Too many requests",
                        Detail = $"Try again in {retryAfterSeconds} seconds."
                    }
                });

                return;
            }
        }

        await _next(context);
    }
}