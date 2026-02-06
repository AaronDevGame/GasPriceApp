public static class ErrorCodes
{
    public const int Ok = 200;

    public const int BadRequest = 400;
    public const int NotFound = 404;

    public const int ServerError = 500;

    // Custom / app-level errors (optional, future-proof)
    public const int ServerAlreadyRunning = 1001;
    public const int ServerAlreadyStopped = 1002;
}