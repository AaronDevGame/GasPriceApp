using Microsoft.AspNetCore.Http.HttpResults;

public static class ApiResults
{
    public static IResult Ok<T>(T data, string? message = null, string? instanceId = null)
        => Results.Ok(new ApiResponse<T>
        {
            Code = ErrorCodes.Ok,
            Message = message,
            InstanceId = instanceId,
            Data = data
        });
    public static IResult BadRequest(string error, string instanceId, string? detail = null)
        => Results.BadRequest(new ApiResponse<object>
        {
            Code = ErrorCodes.BadRequest,
            Message = "bad_request",
            InstanceId = instanceId,
            Error = new ApiError
            {
                Error = error,
                Detail = detail
            }
        });

    public static IResult Unauthorized(string error, string instanceId, string? detail = null)
        => Results.Json(new ApiResponse<object>
        {
            Code = ErrorCodes.Unauthorized,
            Message = "unauthorized_request",
            InstanceId = instanceId,
            Error = new ApiError
            {
                Error = error,
                Detail = detail
            }
        }, statusCode: StatusCodes.Status401Unauthorized);

    public static IResult NotFound(string error, string instanceId, int code, string? detail = null)
        => Results.NotFound(new ApiResponse<object>
        {
            Code = ErrorCodes.NotFound,
            Message = "not_found",
            InstanceId = instanceId,
            Error = new ApiError
            {
                Error = error,
                Detail = detail
            }
        });
}

public static class ApiMetadata
{
    public static readonly ApiInfo Info = new()
    {
        DeveloperName = "Aaron Crisostomo",
        ContactEmail = "aaron@email.com",
        CreatedAt = new DateTime(2026, 2, 6),
        CopyrightNotice = "© 2026 Aaron Crisostomo"
    };
}

public static class AuthErrors
{
    public const string MissingAuthorizationHeader = "missing_authorization_header";
    public const string InvalidAdminKey = "invalid_admin_key";
}