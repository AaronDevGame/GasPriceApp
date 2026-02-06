public class ApiResponse<T>
{
    public int Code { get ; set;}  = 200;
    public string Message { get; set; } = "Success";
    public string InstanceId {get; set; } = ""; 

    public DateTime Date {get; set; } = DateTime.UtcNow;

    public T? Data {get; set;} 
    public ApiError? Error {get; set;}
}

public class ApiError
{
    public string Error {get; set;} = "";
    public string? Detail {get; set;} = "";

}

public class ServerState
{
    public string ServerName { get; set; } = "";
    public string Status { get; set; } = "";
    public string Version { get; set; } = "";
}