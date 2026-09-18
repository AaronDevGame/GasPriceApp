using System;

[Serializable]
public class ApiResponse<T>
{
    public int code;
    public string message;
    public string instanceId;
    public T data;
    public ApiError error;

    public override string ToString()
    {
        object dataObject = data;
        string dataText = dataObject != null ? dataObject.ToString() : "null";
        string errorText = error != null ? error.ToString() : "null";

        return
            $"Code: {code}\n" +
            $"Message: {message}\n" +
            $"Instance ID: {instanceId}\n" +
            $"Data:\n{dataText}\n" +
            $"Error:\n{errorText}";
    }
}

[Serializable]
public class ApiError
{
    public string error;
    public string detail;

    public override string ToString()
    {
        return
            $"Error: {error}\n" +
            $"Detail: {detail}";
    }
}

[Serializable]
public class EmptyData
{
    public override string ToString()
    {
        return "Empty";
    }
}

[Serializable]
public class ServerData
{
    public string serverName;
    public string status;
    public string version;

    public override string ToString()
    {
        return
            $"Server Name: {serverName}\n" +
            $"Status: {status}\n" +
            $"Version: {version}";
    }
}
