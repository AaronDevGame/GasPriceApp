using Microsoft.AspNetCore.Http;

public class AdminAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _adminApiKey;
    private readonly string? _newsApiKey;
    private readonly string? _codexApiKey;
    private readonly string _instanceId;

    public AdminAuthMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;

        _adminApiKey = config["ADMIN_API_KEY"]
            ?? throw new InvalidOperationException("ADMIN_API_KEY is not configured");
        _newsApiKey = config["NEWS_API_KEY"];
        _codexApiKey = config["CODEX_ADMIN_API_KEY"];

        _instanceId = config["INSTANCE_ID"] ?? "unknown";
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (path.StartsWithSegments("/admin"))
        {
            // News credentials never authorize other admin routes, and news has no admin-key fallback.
            var isNews = path.StartsWithSegments(ApiRoutes.AdminFuelNews);
            // This credential authorizes only externally extracted DOE data and its scoped reads.
            var isCodex = path.StartsWithSegments(ApiRoutes.AdminDoeFuelPricesImportExtracted);
            if (!IsAuthorized(context.Request, isNews ? _newsApiKey : isCodex ? _codexApiKey : _adminApiKey,
                isNews ? AuthErrors.InvalidNewsKey : isCodex ? AuthErrors.InvalidCodexKey : AuthErrors.InvalidAdminKey,
                out var authError))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;

                await context.Response.WriteAsJsonAsync(new ApiResponse<object>
                {
                    Code = ErrorCodes.Unauthorized,
                    Message = "unauthorized_request",
                    InstanceId = _instanceId,
                    Error = new ApiError
                    {
                        Error = authError
                    }
                });

                return;
            }
        }

        await _next(context);
    }

    private static bool IsAuthorized(
        HttpRequest request,
        string? expectedKey,
        string invalidKeyError,
        out string error)
    {
        error = string.Empty;

        if (!request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            error = AuthErrors.MissingAuthorizationHeader;
            return false;
        }

        var parts = authHeader.ToString().Split(' ', 2);

        if (string.IsNullOrWhiteSpace(expectedKey) || parts.Length != 2 ||
            !parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            parts[1] != expectedKey)
        {
            error = invalidKeyError;
            return false;
        }

        return true;
    }
}
