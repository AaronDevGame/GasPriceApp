using Microsoft.AspNetCore.Http;

public static class AdminAuthMiddleware
{
    public static async Task InvokeAsync(
        HttpContext context,
        RequestDelegate next,
        string adminApiKey,
        string instanceId)
    {
        var path = context.Request.Path;

        // Protect only admin endpoints
        if (path.StartsWithSegments("/start") ||
            path.StartsWithSegments("/stop"))
        {
            if (!IsAdmin(context.Request, adminApiKey, out var authError))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;

                await context.Response.WriteAsJsonAsync(new ApiResponse<object>
                {
                    Code = ErrorCodes.Unauthorized,
                    Message = "unauthorized_request",
                    InstanceId = instanceId,
                    Error = new ApiError
                    {
                        Error = authError
                    }
                });

                return; // ⛔ stop pipeline
            }
        }

        await next(context); // ✅ continue
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

        var authValue = authHeader.ToString();

        if (string.IsNullOrWhiteSpace(authValue))
        {
            error = AuthErrors.MissingAuthorizationHeader;
            return false;
        }

        var parts = authValue.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 1 &&
            parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            error = AuthErrors.MissingAuthorizationHeader;
            return false;
        }

        if (parts.Length != 2 ||
            !parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            error = AuthErrors.InvalidAdminKey;
            return false;
        }

        var token = parts[1].Trim();

        if (string.IsNullOrWhiteSpace(token))
        {
            error = AuthErrors.MissingAuthorizationHeader;
            return false;
        }

        if (token != adminApiKey)
        {
            error = AuthErrors.InvalidAdminKey;
            return false;
        }

        return true;
    }
}