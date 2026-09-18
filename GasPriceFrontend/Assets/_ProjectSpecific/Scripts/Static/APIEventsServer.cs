using System;
using UnityEngine;

public static class APIEventsServer
{
    public static event Action<APIEndpoint, string> OnRequestSucceeded;
    public static event Action<APIEndpoint, string> OnRequestFailed;

    public static void Ping() => Get<EmptyData>(APIEndpoint.Ping, APIConstants.Endpoints.Ping);
    public static void Health() => Get<ServerData>(APIEndpoint.Health, APIConstants.Endpoints.Health);
    public static void Status() => Get<ServerData>(APIEndpoint.Status, APIConstants.Endpoints.Status);
    public static void Info() => Get<ApiInfoDto>(APIEndpoint.Info, APIConstants.Endpoints.Info);
    public static void Routes() => Get<RouteInfoDto[]>(APIEndpoint.Routes, APIConstants.Endpoints.Routes);

    public static void GuestLogin(string playerName = null)
    {
        WithManager(APIEndpoint.GuestLogin, manager => manager.Post<AuthResultDto>(
            APIConstants.Endpoints.GuestLogin,
            string.IsNullOrWhiteSpace(playerName) ? null : new GuestLoginRequestDto { playerName = playerName },
            APIAuthorization.GuestLogin,
            response =>
            {
                if (response.data != null)
                    manager.SetPlayerSession(response.data.accessToken, response.data.appInstanceId, response.data.guestCredential);
                string safeResult = response.data == null
                    ? "Guest login succeeded."
                    : $"Player: {response.data.playerName}\nPlayer ID: {response.data.playerId}\n" +
                      $"Logged In: {response.data.isLoggedIn}\nToken Expires: {response.data.accessTokenExpiresAt}";
                OnRequestSucceeded?.Invoke(APIEndpoint.GuestLogin, safeResult);
            },
            response => Failure(APIEndpoint.GuestLogin, response)));
    }

    public static void AuthStatus() => Get<AuthStatusDto>(APIEndpoint.AuthStatus, APIConstants.Endpoints.AuthStatus, APIAuthorization.Player);
    public static void Logout()
    {
        WithManager(APIEndpoint.Logout, manager => manager.Post<LogoutDto>(APIConstants.Endpoints.Logout, null,
            APIAuthorization.Player,
            response => { manager.ClearAccessToken(); Success(APIEndpoint.Logout, response); },
            response => Failure(APIEndpoint.Logout, response)));
    }

    public static void GetPlayerData() => Get<PlayerDataDto>(APIEndpoint.GetPlayerData, APIConstants.Endpoints.PlayerData, APIAuthorization.Player);
    public static void PatchPlayerData(PlayerDataPatchDto request) => Patch<PlayerDataDto>(APIEndpoint.PatchPlayerData, APIConstants.Endpoints.PlayerData, request);
    public static void PatchPlayerProfile(string playerName) => Patch<PlayerProfileDto>(APIEndpoint.PatchPlayerProfile,
        APIConstants.Endpoints.PlayerProfile, new PlayerProfilePatchDto { playerName = playerName });
    public static void AiChat(string message) => Post<AiChatResponseDto>(APIEndpoint.AiChat, APIConstants.Endpoints.AiChat,
        new AiChatRequestDto { message = message }, APIAuthorization.Player);
    public static void FuelPrices(double latitude, double longitude) => Post<FuelPriceResponseDto>(APIEndpoint.FuelPrices,
        APIConstants.Endpoints.FuelPrices, new FuelPriceRequestDto { latitude = latitude, longitude = longitude }, APIAuthorization.Player);

    public static void AdminRoutes() => Get<RouteInfoDto[]>(APIEndpoint.AdminRoutes, APIConstants.Endpoints.Admin.Routes, APIAuthorization.Admin);
    public static void AdminChangelog()
    {
        WithManager(APIEndpoint.AdminChangelog, manager => manager.GetText(APIConstants.Endpoints.Admin.Changelog,
            APIAuthorization.Admin,
            text => OnRequestSucceeded?.Invoke(APIEndpoint.AdminChangelog, text),
            error => OnRequestFailed?.Invoke(APIEndpoint.AdminChangelog, error)));
    }
    public static void AdminStatus() => Get<ServerData>(APIEndpoint.AdminStatus, APIConstants.Endpoints.Admin.Status, APIAuthorization.Admin);
    public static void AdminStart() => Post<ServerData>(APIEndpoint.AdminStart, APIConstants.Endpoints.Admin.Start, null, APIAuthorization.Admin);
    public static void AdminStop() => Post<ServerData>(APIEndpoint.AdminStop, APIConstants.Endpoints.Admin.Stop, null, APIAuthorization.Admin);
    public static void AdminRestart() => Post<ServerData>(APIEndpoint.AdminRestart, APIConstants.Endpoints.Admin.Restart, null, APIAuthorization.Admin);

    private static void Get<T>(APIEndpoint endpoint, string route, APIAuthorization authorization = APIAuthorization.None) =>
        WithManager(endpoint, manager => manager.Get<T>(route, authorization,
            response => Success(endpoint, response), response => Failure(endpoint, response)));

    private static void Post<T>(APIEndpoint endpoint, string route, object body, APIAuthorization authorization) =>
        WithManager(endpoint, manager => manager.Post<T>(route, body, authorization,
            response => Success(endpoint, response), response => Failure(endpoint, response)));

    private static void Patch<T>(APIEndpoint endpoint, string route, object body) =>
        WithManager(endpoint, manager => manager.Patch<T>(route, body, APIAuthorization.Player,
            response => Success(endpoint, response), response => Failure(endpoint, response)));

    private static void WithManager(APIEndpoint endpoint, Action<APIManager> action)
    {
        if (APIManager.Instance == null)
        {
            OnRequestFailed?.Invoke(endpoint, "APIManager instance is missing from the scene.");
            return;
        }
        try { action(APIManager.Instance); }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            OnRequestFailed?.Invoke(endpoint, exception.Message);
        }
    }

    private static void Success<T>(APIEndpoint endpoint, ApiResponse<T> response) =>
        OnRequestSucceeded?.Invoke(endpoint, response?.ToString() ?? "Empty response");
    private static void Failure<T>(APIEndpoint endpoint, ApiResponse<T> response) =>
        OnRequestFailed?.Invoke(endpoint, response?.ToString() ?? "Request failed before a response was received.");
}

public enum APIEndpoint
{
    Ping, Health, Status, Info, Routes, GuestLogin, AuthStatus, Logout,
    GetPlayerData, PatchPlayerData, PatchPlayerProfile, AiChat, FuelPrices,
    AdminRoutes, AdminChangelog, AdminStatus, AdminStart, AdminStop, AdminRestart
}
