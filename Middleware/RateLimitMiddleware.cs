using Microsoft.AspNetCore.Http;

public class RateLimitMiddleware
{
    private static readonly Dictionary<string, TimeSpan> _cooldowns = new(StringComparer.OrdinalIgnoreCase)
    {
        [ApiRoutes.Ping] = TimeSpan.FromSeconds(1),
        [ApiRoutes.Health] = TimeSpan.FromSeconds(1),
        [ApiRoutes.Status] = TimeSpan.FromSeconds(1),
        [ApiRoutes.Info] = TimeSpan.FromSeconds(1),
        [ApiRoutes.Routes] = TimeSpan.FromSeconds(1),
        [ApiRoutes.AuthStatus] = TimeSpan.FromSeconds(1),
        [ApiRoutes.AuthGuestLogin] = TimeSpan.FromSeconds(1),
        [ApiRoutes.AuthLogout] = TimeSpan.FromSeconds(1),
        [ApiRoutes.PlayerData] = TimeSpan.FromSeconds(1),
        [ApiRoutes.PlayerProfile] = TimeSpan.FromSeconds(1),
        [ApiRoutes.AiChat] = TimeSpan.FromSeconds(5),
    };

    // Key = ip + "|" + route
    private readonly Dictionary<string, DateTimeOffset> _lastRequest = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _guestLoginAttempts = new();
    private DateTimeOffset _lastCleanup;
    private readonly RequestDelegate _next;
    private readonly TimeProvider _timeProvider;
    private readonly int _guestLoginsPerFiveMinutes;

    public RateLimitMiddleware(RequestDelegate next, IConfiguration configuration, TimeProvider timeProvider)
    {
        _next = next;
        _timeProvider = timeProvider;
        _lastCleanup = timeProvider.GetUtcNow();
        _guestLoginsPerFiveMinutes = configuration.GetValue<int?>("RateLimiting:GuestLogin:PermitLimitPerFiveMinutes") ?? 10;
        if (_guestLoginsPerFiveMinutes <= 0)
            throw new InvalidOperationException("The guest login rate limit must be a positive integer.");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        var ip = (address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address)?.ToString() ?? "unknown";
        // Routing accepts case changes and trailing slashes; they must share a bucket.
        var path = new PathString((context.Request.Path.Value ?? "").TrimEnd('/').ToLowerInvariant());
        var isGuestLogin = path == ApiRoutes.AuthGuestLogin;

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
        bool blocked = false;
        int retryAfterSeconds = 0;

        lock (_lastRequest)
        {
            var now = _timeProvider.GetUtcNow();
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

                // Keep login history for the full five-minute window, independently
                // of the shorter cooldown history. Remove inactive IP buckets.
                foreach (var loginIp in _guestLoginAttempts.Keys.ToArray())
                {
                    TrimLoginAttempts(_guestLoginAttempts[loginIp], now);
                    if (_guestLoginAttempts[loginIp].Count == 0)
                        _guestLoginAttempts.Remove(loginIp);
                }
            }

            if (_lastRequest.TryGetValue(key, out var lastTime))
            {
                var elapsed = now - lastTime;
                if (elapsed < cooldown)
                {
                    blocked = true;
                    retryAfterSeconds = (int)Math.Ceiling((cooldown - elapsed).TotalSeconds);
                }
            }

            if (isGuestLogin)
            {
                if (!_guestLoginAttempts.TryGetValue(ip, out var attempts))
                {
                    attempts = new Queue<DateTimeOffset>();
                    _guestLoginAttempts[ip] = attempts;
                }

                TrimLoginAttempts(attempts, now);
                if (attempts.Count >= _guestLoginsPerFiveMinutes)
                    retryAfterSeconds = Math.Max(retryAfterSeconds,
                        RetryAfter(attempts.Peek().AddMinutes(5), now));

                blocked |= retryAfterSeconds > 0;
                // Reserve before invoking the endpoint so concurrent requests
                // cannot exceed the limit. Failed logins consume permits too.
                if (!blocked)
                    attempts.Enqueue(now);
            }

            if (!blocked)
                _lastRequest[key] = now;
        }

        if (blocked)
        {
            context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
            await ApiResults.TooManyRequest(
                "Too many requests",
                $"Try again in {retryAfterSeconds} seconds.",
                message: "too_many_requests").ExecuteAsync(context);

            return;
        }

        await _next(context);
    }

    private static void TrimLoginAttempts(Queue<DateTimeOffset> attempts, DateTimeOffset now)
    {
        while (attempts.TryPeek(out var oldest) && oldest <= now.AddMinutes(-5))
            attempts.Dequeue();
    }

    private static int RetryAfter(DateTimeOffset availableAt, DateTimeOffset now) =>
        Math.Max(1, (int)Math.Ceiling((availableAt - now).TotalSeconds));
}
