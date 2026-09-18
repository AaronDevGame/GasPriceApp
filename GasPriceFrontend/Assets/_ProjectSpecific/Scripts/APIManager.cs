using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

public class APIManager : ASingleton<APIManager>
{

    public void Get<T>(
        string endpoint,
        Dictionary<string, string> headers = null,
        Action<ApiResponse<T>> onSuccess = null,
        Action<ApiResponse<T>> onError = null)
    {
        SendRequest<T>(endpoint, UnityWebRequest.kHttpVerbGET, null, headers, onSuccess, onError);
    }

    public void Post<T>(
        string endpoint,
        object body = null,
        Dictionary<string, string> headers = null,
        Action<ApiResponse<T>> onSuccess = null,
        Action<ApiResponse<T>> onError = null)
    {
        SendRequest<T>(endpoint, UnityWebRequest.kHttpVerbPOST, body, headers, onSuccess, onError);
    }

    private void SendRequest<T>(
        string endpoint,
        string method,
        object body,
        Dictionary<string, string> headers,
        Action<ApiResponse<T>> onSuccess,
        Action<ApiResponse<T>> onError)
    {
        StartCoroutine(SendRequestCoroutine<T>(endpoint, method, body, headers, onSuccess, onError));
    }

    private IEnumerator SendRequestCoroutine<T>(
        string endpoint,
        string method,
        object body,
        Dictionary<string, string> headers,
        Action<ApiResponse<T>> onSuccess,
        Action<ApiResponse<T>> onError)
    {
        string url = $"{API.BaseUrl}{endpoint}";
        Debug.Log($"<color=yellow>Url: {url}</color>");
        
        using var webRequest = new UnityWebRequest(url, method);

        webRequest.downloadHandler = new DownloadHandlerBuffer();
        // webRequest.timeout = timeout;

        bool hasBody =
            body != null &&
            (method == UnityWebRequest.kHttpVerbPOST || method == UnityWebRequest.kHttpVerbPUT);

        if (hasBody)
        {
            string jsonBody = JsonUtility.ToJson(body);
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);

            webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);

            Debug.Log($"[API] Request Body: {jsonBody}");
        }

        webRequest.SetRequestHeader("Accept", "application/json");
        if (hasBody)
        {
            webRequest.SetRequestHeader("Content-Type", "application/json");
        }

        if (headers != null)
        {
            foreach (KeyValuePair<string, string> header in headers)
            {
                webRequest.SetRequestHeader(header.Key, header.Value);
            }
        }
        
        float startTime = Time.realtimeSinceStartup;

        Debug.Log("<i>requesting...</i>");
        yield return webRequest.SendWebRequest();

        float duration = Time.realtimeSinceStartup - startTime;
    
        // get the raw response
        string rawResponse = webRequest.downloadHandler.text;
        Debug.Log($"<color=green>{duration:0.00}s</color> Response: {rawResponse}");

        ApiResponse<T> response = null;
        if (!string.IsNullOrWhiteSpace(rawResponse))
        {
            try
            {
                response = JsonUtility.FromJson<ApiResponse<T>>(rawResponse);
            }
            catch (ArgumentException exception)
            {
                Debug.LogError($"[API] JSON Parse Error: {exception.Message}");
                onError?.Invoke(null);
                yield break;
            }
        }

        if (webRequest.result == UnityWebRequest.Result.ConnectionError || webRequest.result == UnityWebRequest.Result.ProtocolError)
        {
            Debug.LogError($"[API] HTTP Error: {webRequest.responseCode} | {webRequest.error}");
            onError?.Invoke(response);
        } 
        else if (response == null)
        {
            Debug.LogError("Response is null");
            onError?.Invoke(response);
        }
        else if (response.code >= 400)
        {
            Debug.LogError($"Backend Error: {response.code} - {response.message}");
            onError?.Invoke(response);
            
        } else
        {
            onSuccess?.Invoke(response);
        }
    }
}
