using System;
using UnityEngine;

[Serializable]
public class ApiResponse<T>
{
    public int code;
    public string message;
    public string instanceId;
    public string date;
    public string processingTime;
    public T data;
    public ApiError error;
    [NonSerialized] public string rawJson;
    [NonSerialized] public long httpStatus;

    public static ApiResponse<T> LocalError(string message, string rawJson = "") => new()
    {
        code = 0,
        message = "client_error",
        error = new ApiError { error = message },
        rawJson = rawJson
    };

    public override string ToString()
    {
        if (!string.IsNullOrWhiteSpace(rawJson)) return rawJson;
        return JsonUtility.ToJson(this, true);
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
    public string startedAt;
    public string lastStartedAt;
    public string lastStoppedAt;
    public int restartCount;
    public double uptimeSeconds;
    public string uptime;
    public double totalUptimeSeconds;
    public string totalUptime;

    public override string ToString()
    {
        return
            $"Server Name: {serverName}\n" +
            $"Status: {status}\n" +
            $"Version: {version}";
    }
}

[Serializable] public class ApiInfoDto { public string developerName; public string contactEmail; public string createdAt; public string copyrightNotice; public string version; }
[Serializable] public class RouteInfoDto { public string route; public string method; public bool isLegacy; }
[Serializable] public class RouteListDto { public RouteInfoDto[] items; }

[Serializable] public class GuestLoginRequestDto { public string playerName; }
[Serializable] public class AuthResultDto
{
    public string appInstanceId; public long playerId; public string playerName; public string accessToken;
    public string accessTokenExpiresAt; public string guestCredential; public string tokenType;
    public string createdAt; public bool isNewAccount; public bool isLoggedIn;
}
[Serializable] public class AuthStatusDto
{
    public bool hasGuestLogin; public bool isLoggedIn; public string playerName; public long playerId;
    public string accountType; public string createdAt; public string lastLoginAt;
}
[Serializable] public class LogoutDto { public bool isLoggedIn; }

[Serializable] public class PlayerDataDto
{
    public long playerId; public int health; public long money; public PositionDto position;
    public InventoryItemDto[] inventory; public ExtraDataDto extraData; public string createdAt; public string updatedAt;
}
[Serializable] public class PositionDto { public float x; public float y; public float z; }
[Serializable] public class InventoryItemDto { public string id; public int quantity; }
[Serializable] public class ExtraDataDto { public string note; }
[Serializable] public class PlayerDataPatchDto
{
    public int health; public long money; public PositionDto position; public InventoryItemDto[] inventory; public ExtraDataDto extraData;
}
[Serializable] public class PlayerProfilePatchDto { public string playerName; }
[Serializable] public class PlayerProfileDto { public long playerId; public string playerName; }

[Serializable] public class AiChatRequestDto { public string message; }
[Serializable] public class AiChatResponseDto
{
    public string message; public string model; public AiTokenUsageDto usage; public bool usedWebSearch; public AiSourceDto[] sources;
}
[Serializable] public class AiSourceDto { public string title; public string url; }
[Serializable] public class AiTokenUsageDto { public int inputTokens; public int cachedInputTokens; public int outputTokens; public int totalTokens; }

[Serializable] public class FuelPriceRequestDto { public double latitude; public double longitude; }
[Serializable] public class FuelPriceResponseDto
{
    // result is backend-agent-defined. JsonUtility fills known fields while rawJson retains the complete payload.
    public FuelPriceResultDto result; public string model; public AiTokenUsageDto usage; public CostEstimateDto estimatedCost;
    public bool usedWebSearch; public bool fromCache; public bool cacheStored; public string cacheScope;
    public string cachedAt; public string refreshAfter;
}
[Serializable] public class FuelPriceResultDto { public FuelLocationDto location; public string data_as_of; public FuelEstimateAreaDto estimate_area; }
[Serializable] public class FuelLocationDto { public double latitude; public double longitude; public string resolved_area; public string city; public string province; public string region; public string country; }
[Serializable] public class FuelEstimateAreaDto { public string level; public string name; }
[Serializable] public class CostEstimateDto
{
    public string currency; public double inputCost; public double outputCost; public double webSearchCost; public double totalCost;
    public double inputPricePerMillionTokens; public double cachedInputPricePerMillionTokens;
    public double outputPricePerMillionTokens; public double webSearchPricePerCall; public int webSearchCalls;
}
