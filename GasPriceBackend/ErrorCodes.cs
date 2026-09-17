public static class ErrorCodes
{
    public const int Ok = 200;

    public const int BadRequest = 400;
    public const int Unauthorized = 401;
    public const int NotFound = 404;
    public const int TooManyRequest = 429;
    public const int ServerError = 500;
    public const int BadGateway = 502;
    public const int ServiceUnavailable = 503;
    public const int GatewayTimeout = 504;

    // Custom / app-level errors (optional, future-proof)
    public const int ServerAlreadyRunning = 1001;
    public const int ServerAlreadyStopped = 1002;
}
