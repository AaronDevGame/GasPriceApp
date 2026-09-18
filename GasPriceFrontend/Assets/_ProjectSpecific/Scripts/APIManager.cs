using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class APIManager : ASingleton<APIManager>
{
    // Credentials are deliberately runtime-only: never serialized and never written to PlayerPrefs.
    [NonSerialized] private string accessToken;
    [NonSerialized] private string guestCredential;
    [NonSerialized] private string adminApiKey;
    [NonSerialized] private string appInstanceId;

    public string AppInstanceId => appInstanceId;
    public bool HasPlayerAuthorization => !string.IsNullOrWhiteSpace(accessToken);

    protected override void Awake()
    {
        base.Awake();
        if (Instance == this && string.IsNullOrEmpty(appInstanceId))
            appInstanceId = Guid.NewGuid().ToString("D");
    }

    public void SetPlayerSession(string token, string instanceId, string credential = null)
    {
        accessToken = token?.Trim();
        if (!string.IsNullOrWhiteSpace(credential)) guestCredential = credential.Trim();
        if (!string.IsNullOrWhiteSpace(instanceId)) appInstanceId = instanceId.Trim();
    }

    public void ClearAccessToken() => accessToken = null;

    public void ClearPlayerSession()
    {
        accessToken = null;
        guestCredential = null;
    }

    public void SetAdminApiKey(string value) => adminApiKey = value?.Trim();

    public void Get<T>(string endpoint, APIAuthorization authorization = APIAuthorization.None,
        Action<ApiResponse<T>> onSuccess = null, Action<ApiResponse<T>> onError = null) =>
        Send(endpoint, UnityWebRequest.kHttpVerbGET, null, authorization, onSuccess, onError);

    public void Post<T>(string endpoint, object body = null, APIAuthorization authorization = APIAuthorization.None,
        Action<ApiResponse<T>> onSuccess = null, Action<ApiResponse<T>> onError = null) =>
        Send(endpoint, UnityWebRequest.kHttpVerbPOST, body, authorization, onSuccess, onError);

    public void Patch<T>(string endpoint, object body, APIAuthorization authorization,
        Action<ApiResponse<T>> onSuccess = null, Action<ApiResponse<T>> onError = null) =>
        Send(endpoint, "PATCH", body, authorization, onSuccess, onError);

    public void GetText(string endpoint, APIAuthorization authorization,
        Action<string> onSuccess, Action<string> onError) =>
        StartCoroutine(SendTextCoroutine(endpoint, authorization, onSuccess, onError));

    private void Send<T>(string endpoint, string method, object body, APIAuthorization authorization,
        Action<ApiResponse<T>> onSuccess, Action<ApiResponse<T>> onError) =>
        StartCoroutine(SendCoroutine(endpoint, method, body, authorization, onSuccess, onError));

    private IEnumerator SendCoroutine<T>(string endpoint, string method, object body,
        APIAuthorization authorization, Action<ApiResponse<T>> onSuccess, Action<ApiResponse<T>> onError)
    {
        UnityWebRequest request;
        try { request = CreateRequest(endpoint, method, body, authorization); }
        catch (Exception exception)
        {
            onError?.Invoke(ApiResponse<T>.LocalError(exception.Message));
            yield break;
        }

        using (request)
        {
            float startedAt = Time.realtimeSinceStartup;
            yield return request.SendWebRequest();
            string raw = request.downloadHandler?.text ?? string.Empty;
            Debug.Log($"[API] {method} {endpoint} -> {request.responseCode} ({Time.realtimeSinceStartup - startedAt:0.00}s)");

            ApiResponse<T> response;
            try { response = string.IsNullOrWhiteSpace(raw) ? null : JsonUtility.FromJson<ApiResponse<T>>(raw); }
            catch (Exception exception) { response = ApiResponse<T>.LocalError($"Invalid JSON response: {exception.Message}", raw); }

            response ??= ApiResponse<T>.LocalError(request.error ?? "The server returned an empty response.", raw);
            response.rawJson = raw;
            response.httpStatus = request.responseCode;
            bool failed = request.result is UnityWebRequest.Result.ConnectionError or UnityWebRequest.Result.ProtocolError || response.code >= 400;
            if (failed) onError?.Invoke(response); else onSuccess?.Invoke(response);
        }
    }

    private IEnumerator SendTextCoroutine(string endpoint, APIAuthorization authorization,
        Action<string> onSuccess, Action<string> onError)
    {
        UnityWebRequest request;
        try { request = CreateRequest(endpoint, UnityWebRequest.kHttpVerbGET, null, authorization); }
        catch (Exception exception) { onError?.Invoke(exception.Message); yield break; }

        using (request)
        {
            yield return request.SendWebRequest();
            string text = request.downloadHandler?.text ?? string.Empty;
            if (request.result == UnityWebRequest.Result.Success) onSuccess?.Invoke(text);
            else onError?.Invoke($"HTTP {request.responseCode}: {request.error}\n{text}");
        }
    }

    private UnityWebRequest CreateRequest(string endpoint, string method, object body, APIAuthorization authorization)
    {
        var request = new UnityWebRequest(APIConstants.BaseUrl.TrimEnd('/') + endpoint, method)
        {
            downloadHandler = new DownloadHandlerBuffer(), timeout = APIConstants.TimeoutSeconds
        };
        request.SetRequestHeader(APIConstants.Headers.Accept, APIConstants.Headers.Json);
        if (body != null)
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body)));
            request.SetRequestHeader(APIConstants.Headers.ContentType, APIConstants.Headers.Json);
        }
        ApplyAuthorization(request, authorization);
        return request;
    }

    private void ApplyAuthorization(UnityWebRequest request, APIAuthorization authorization)
    {
        if (authorization == APIAuthorization.Player)
        {
            if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("Call Guest Login before an authorized player endpoint.");
            request.SetRequestHeader(APIConstants.Headers.Authorization, APIConstants.Headers.BearerPrefix + accessToken);
            request.SetRequestHeader(APIConstants.Headers.AppInstanceId, appInstanceId);
        }
        else if (authorization == APIAuthorization.GuestLogin)
        {
            request.SetRequestHeader(APIConstants.Headers.AppInstanceId, appInstanceId);
            if (!string.IsNullOrWhiteSpace(guestCredential)) request.SetRequestHeader(APIConstants.Headers.GuestCredential, guestCredential);
        }
        else if (authorization == APIAuthorization.Admin)
        {
            if (string.IsNullOrWhiteSpace(adminApiKey)) throw new InvalidOperationException("Set the admin API key at runtime first.");
            request.SetRequestHeader(APIConstants.Headers.Authorization, APIConstants.Headers.BearerPrefix + adminApiKey);
        }
    }
}

public enum APIAuthorization { None, GuestLogin, Player, Admin }
