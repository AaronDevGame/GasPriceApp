using Microsoft.AspNetCore.Http;

public class RateLimitMiddleware
{
    private static readonly Dictionary<string, TimeSpan> _cooldowns = new()
    {
        [ApiRoutes.Ping] = TimeSpan.FromSeconds(1),
        [ApiRoutes.Health] = TimeSpan.FromSeconds(1),
        [ApiRoutes.Status] = TimeSpan.FromSeconds(1),
        [ApiRoutes.Info] = TimeSpan.FromSeconds(1),
        [ApiRoutes.Routes] = TimeSpan.FromSeconds(1),
        [ApiRoutes.AuthStatus] = TimeSpan.FromSeconds(1),
        [ApiRoutes.AuthGuestLogin] = TimeSpan.FromSeconds(1),
        [ApiRoutes.AuthLogout] = TimeSpan.FromSeconds(1),
        [ApiRoutes.LegacyLogin] = TimeSpan.FromSeconds(1),
        [ApiRoutes.LegacyLogout] = TimeSpan.FromSeconds(1),
        [ApiRoutes.PlayerData] = TimeSpan.FromSeconds(1),
    };

    // Key = ip + "|" + route
    private static readonly Dictionary<string, DateTime> _lastRequest = new();
    private static DateTime _lastCleanup = DateTime.UtcNow;
    private readonly RequestDelegate _next;

    public RateLimitMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var path = context.Request.Path;

        // 1) Decide cooldown
        TimeSpan cooldown;

        // Admin: rate-limit all /admin/* uniformly
        if (path.StartsWithSegments("/admin"))
        {
            cooldown = TimeSpan.FromSeconds(1);
        }
        else if (_cooldowns.TryGetValue(path.Value ?? "", out var specificCooldown))
        {
            cooldown = specificCooldown;
        }
        else
        {
            cooldown = TimeSpan.FromSeconds(1); // unknown endpoints
        }

        // 1.5) Decide bucket key (prevents bypass by changing path)
        var keyPath =
            path.StartsWithSegments("/admin") ? "/admin" :
            _cooldowns.ContainsKey(path.Value ?? "") ? (path.Value ?? "") :
            "/unknown";

        var key = $"{ip}|{keyPath}";
        var now = DateTime.UtcNow;

        bool blocked = false;
        int retryAfterSeconds = 0;

        lock (_lastRequest)
        {
            if ((now - _lastCleanup).TotalMinutes >= 1)
            {
                var cutoff = now.AddMinutes(-10);

                List<string>? toRemove = null;
                foreach (var kvp in _lastRequest)
                {
                    if (kvp.Value < cutoff)
                    {
                        toRemove ??= new List<string>();
                        toRemove.Add(kvp.Key);
                    }
                }

                if (toRemove != null)
                {
                    foreach (var k in toRemove)
                    {
                        // Console.WriteLine($"[CLEANUP] Removing: {k}");
                        _lastRequest.Remove(k);
                    }
                }

                // Console.WriteLine($"[CLEANUP] Remaining count: {_lastRequest.Count}");

                _lastCleanup = now;
            }

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

        await _next(context);
    }
}
