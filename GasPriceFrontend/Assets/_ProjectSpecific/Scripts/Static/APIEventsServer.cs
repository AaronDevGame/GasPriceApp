using System;
using UnityEngine;

public static class APIEventsServer
{
    public static event Action<ApiResponse<EmptyData>> OnPingSuccess;
    public static event Action<ApiResponse<EmptyData>> OnPingError;

    public static event Action<ApiResponse<ServerData>> OnHealthSuccess;
    public static event Action<ApiResponse<ServerData>> OnHealthError;

    public static event Action<ApiResponse<ServerData>> OnStatusSuccess;
    public static event Action<ApiResponse<ServerData>> OnStatusError;

    public static event Action<ApiResponse<ServerData>> OnStartServerSuccess;
    public static event Action<ApiResponse<ServerData>> OnStartServerError;

    public static event Action<ApiResponse<ServerData>> OnStopServerSuccess;
    public static event Action<ApiResponse<ServerData>> OnStopServerError;

    public static void Ping()
    {
        if (!TryGetManager(OnPingError, out APIManager apiManager))
            return;

        apiManager.Get<EmptyData>(
            API.Endpoints.Ping,
            onSuccess: InvokePingSuccess,
            onError: InvokePingError);
    }

    public static void Health()
    {
        if (!TryGetManager(OnHealthError, out APIManager apiManager))
            return;

        apiManager.Get<ServerData>(
            API.Endpoints.Health,
            onSuccess: InvokeHealthSuccess,
            onError: InvokeHealthError);
    }

    public static void Status()
    {
        if (!TryGetManager(OnStatusError, out APIManager apiManager))
            return;

        apiManager.Get<ServerData>(
            API.Endpoints.Status,
            onSuccess: InvokeStatusSuccess,
            onError: InvokeStatusError);
    }

    public static void StartServer(string adminBearerToken)
    {
        if (string.IsNullOrWhiteSpace(adminBearerToken))
        {
            const string message = "Admin bearer token is empty.";
            Debug.LogError(message);
            InvokeStartServerError(CreateLocalError<ServerData>(message));
            return;
        }

        if (!TryGetManager(OnStartServerError, out APIManager apiManager))
            return;

        apiManager.Post<ServerData>(
            API.Endpoints.Admin.Start,
            headers: APIHeaders.Bearer(adminBearerToken),
            onSuccess: InvokeStartServerSuccess,
            onError: InvokeStartServerError);
    }

    public static void StopServer(string adminBearerToken)
    {
        if (string.IsNullOrWhiteSpace(adminBearerToken))
        {
            const string message = "Admin bearer token is empty.";
            Debug.LogError(message);
            InvokeStopServerError(CreateLocalError<ServerData>(message));
            return;
        }

        if (!TryGetManager(OnStopServerError, out APIManager apiManager))
            return;

        apiManager.Post<ServerData>(
            API.Endpoints.Admin.Stop,
            headers: APIHeaders.Bearer(adminBearerToken),
            onSuccess: InvokeStopServerSuccess,
            onError: InvokeStopServerError);
    }

    private static bool TryGetManager<T>(Action<ApiResponse<T>> onError, out APIManager apiManager)
    {
        apiManager = APIManager.Instance;
        if (apiManager != null)
            return true;

        const string message = "APIManager instance is missing from the scene.";
        Debug.LogError(message);
        onError?.Invoke(CreateLocalError<T>(message));
        return false;
    }

    private static ApiResponse<T> CreateLocalError<T>(string message)
    {
        return new ApiResponse<T>
        {
            code = 0,
            message = message
        };
    }

    private static void InvokePingSuccess(ApiResponse<EmptyData> response) => OnPingSuccess?.Invoke(response);
    private static void InvokePingError(ApiResponse<EmptyData> response) => OnPingError?.Invoke(response);

    private static void InvokeHealthSuccess(ApiResponse<ServerData> response) => OnHealthSuccess?.Invoke(response);
    private static void InvokeHealthError(ApiResponse<ServerData> response) => OnHealthError?.Invoke(response);

    private static void InvokeStatusSuccess(ApiResponse<ServerData> response) => OnStatusSuccess?.Invoke(response);
    private static void InvokeStatusError(ApiResponse<ServerData> response) => OnStatusError?.Invoke(response);

    private static void InvokeStartServerSuccess(ApiResponse<ServerData> response) => OnStartServerSuccess?.Invoke(response);
    private static void InvokeStartServerError(ApiResponse<ServerData> response) => OnStartServerError?.Invoke(response);

    private static void InvokeStopServerSuccess(ApiResponse<ServerData> response) => OnStopServerSuccess?.Invoke(response);
    private static void InvokeStopServerError(ApiResponse<ServerData> response) => OnStopServerError?.Invoke(response);
}
