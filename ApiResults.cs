using Microsoft.AspNetCore.Http.HttpResults;

public static class ApiResults
{
    public static IResult Ok<T>(T data, string message, string instanceId)
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