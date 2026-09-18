using NaughtyAttributes;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class APICallingSampleScript : MonoBehaviour
{
    [SerializeField] private APIEndpoint endpoint;
    [SerializeField] private Button requestButton;
    [SerializeField] private TMP_Text responseText;
    [SerializeField] private TMP_InputField runtimeAdminKeyInput;
    [SerializeField] private string playerName = "UnityPlayer";
    [SerializeField, TextArea] private string chatMessage = "What are today's fuel price trends?";
    [SerializeField] private int health = 100;
    [SerializeField] private long money;
    [SerializeField] private double latitude = 14.5995;
    [SerializeField] private double longitude = 120.9842;

    public void Configure(APIEndpoint configuredEndpoint, Button button, TMP_Text output, TMP_InputField adminKeyInput = null)
    {
        endpoint = configuredEndpoint;
        requestButton = button;
        responseText = output;
        runtimeAdminKeyInput = adminKeyInput;
    }

    private void OnEnable()
    {
        if (requestButton != null) requestButton.onClick.AddListener(CallEndpoint);
        APIEventsServer.OnRequestSucceeded += OnSuccess;
        APIEventsServer.OnRequestFailed += OnFailed;
    }

    private void OnDisable()
    {
        if (requestButton != null) requestButton.onClick.RemoveListener(CallEndpoint);
        APIEventsServer.OnRequestSucceeded -= OnSuccess;
        APIEventsServer.OnRequestFailed -= OnFailed;
    }

    [Button("Call Endpoint")]
    public void CallEndpoint()
    {
        if (responseText != null) responseText.text = $"Calling {endpoint}...";
        if (APIManager.Instance != null && runtimeAdminKeyInput != null && !string.IsNullOrWhiteSpace(runtimeAdminKeyInput.text))
            APIManager.Instance.SetAdminApiKey(runtimeAdminKeyInput.text);

        switch (endpoint)
        {
            case APIEndpoint.Ping: APIEventsServer.Ping(); break;
            case APIEndpoint.Health: APIEventsServer.Health(); break;
            case APIEndpoint.Status: APIEventsServer.Status(); break;
            case APIEndpoint.Info: APIEventsServer.Info(); break;
            case APIEndpoint.Routes: APIEventsServer.Routes(); break;
            case APIEndpoint.GuestLogin: APIEventsServer.GuestLogin(playerName); break;
            case APIEndpoint.AuthStatus: APIEventsServer.AuthStatus(); break;
            case APIEndpoint.Logout: APIEventsServer.Logout(); break;
            case APIEndpoint.GetPlayerData: APIEventsServer.GetPlayerData(); break;
            case APIEndpoint.PatchPlayerData:
                APIEventsServer.PatchPlayerData(new PlayerDataPatchDto
                {
                    health = health, money = money, position = new PositionDto(),
                    inventory = new InventoryItemDto[0], extraData = new ExtraDataDto()
                });
                break;
            case APIEndpoint.PatchPlayerProfile: APIEventsServer.PatchPlayerProfile(playerName); break;
            case APIEndpoint.AiChat: APIEventsServer.AiChat(chatMessage); break;
            case APIEndpoint.FuelPrices: APIEventsServer.FuelPrices(latitude, longitude); break;
            case APIEndpoint.AdminRoutes: APIEventsServer.AdminRoutes(); break;
            case APIEndpoint.AdminChangelog: APIEventsServer.AdminChangelog(); break;
            case APIEndpoint.AdminStatus: APIEventsServer.AdminStatus(); break;
            case APIEndpoint.AdminStart: APIEventsServer.AdminStart(); break;
            case APIEndpoint.AdminStop: APIEventsServer.AdminStop(); break;
            case APIEndpoint.AdminRestart: APIEventsServer.AdminRestart(); break;
        }
    }

    private void OnSuccess(APIEndpoint completedEndpoint, string result)
    {
        if (completedEndpoint != endpoint) return;
        if (responseText != null) responseText.text = $"SUCCESS\n{result}";
    }

    private void OnFailed(APIEndpoint failedEndpoint, string error)
    {
        if (failedEndpoint != endpoint) return;
        if (responseText != null) responseText.text = $"FAILED\n{error}";
    }
}
