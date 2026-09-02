using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc.ApiExplorer;

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

    public static IResult NotFound(string error, string instanceId, string path)
        => Results.Json(new ApiResponse<object>
        {
            Code = ErrorCodes.NotFound,
            Message = "not_found",
            InstanceId = instanceId,
            Error = new ApiError
            {
                Error = error,
                Detail = $"Path {path} doesn't exist."
            }
        }, statusCode: StatusCodes.Status404NotFound);

    public static IResult TooManyRequest(string error, string? detail = null)
        => Results.Json(new ApiResponse<object>
        {
            Code = ErrorCodes.TooManyRequest,
            Message = "too_many_request",
            Error = new ApiError
            {
                Error = error,
                Detail = detail
            }
        }, statusCode: StatusCodes.Status429TooManyRequests);
}

public static class ApiMetadata
{
    public static readonly ApiInfo Info = new()
    {
        DeveloperName = "Aaron Crisostomo",
        ContactEmail = "aarondevgame@gmail.com",
        CreatedAt = new DateTime(2026, 2, 6),
        CopyrightNotice = "© 2026 Aaron Crisostomo"
    };
}

public static class AuthErrors
{
    public const string MissingAuthorizationHeader = "missing_authorization_header";
    public const string MissingDeviceIdHeader = "missing_device_id_header";
    public const string InvalidAdminKey = "invalid_admin_key";
    public const string InvalidPlayerToken = "invalid_player_token";
    public const string InvalidPlayerCredentials = "invalid_player_credentials";
    public const string PlayerNotLoggedIn = "player_not_logged_in";
    public const string MissingGuestCredential = "missing_guest_credential";
    public const string InvalidGuestCredential = "invalid_guest_credential";
    public const string LegacyTokenRequired = "legacy_token_required";
    public const string AccessTokenExpired = "access_token_expired";
}
