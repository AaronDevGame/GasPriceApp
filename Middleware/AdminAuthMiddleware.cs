using Microsoft.AspNetCore.Http;

public class AdminAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _adminApiKey;
    private readonly string _instanceId;

    public AdminAuthMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;

        _adminApiKey = config["ADMIN_API_KEY"]
            ?? throw new InvalidOperationException("ADMIN_API_KEY is not configured");

        _instanceId = config["INSTANCE_ID"] ?? "unknown";
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (path.StartsWithSegments("/admin"))
        {
            if (!IsAdmin(context.Request, _adminApiKey, out var authError))
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

    private static bool IsAdmin(
        HttpRequest request,
        string adminApiKey,
        out string error)
    {
        error = string.Empty;

        if (!request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            error = AuthErrors.MissingAuthorizationHeader;
            return false;
        }

        var parts = authHeader.ToString().Split(' ', 2);

        if (parts.Length != 2 ||
            !parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            parts[1] != adminApiKey)
        {
            error = AuthErrors.InvalidAdminKey;
            return false;
        }

        return true;
    }
}
