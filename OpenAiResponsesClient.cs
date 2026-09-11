using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class OpenAiResponsesClient
{
    private const string DefaultModel = "gpt-5.6-luna";
    private const int MaxOutputTokens = 500;

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiResponsesClient> _logger;
    private readonly string? _apiKey;

    public OpenAiResponsesClient(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<OpenAiResponsesClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = configuration["OPENAI_API_KEY"];

        var configuredModel = configuration["OPENAI_MODEL"];
        Model = string.IsNullOrWhiteSpace(configuredModel)
            ? DefaultModel
            : configuredModel.Trim();
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);
    public string Model { get; }

    public async Task<OpenAiResponseResult> CreateResponseAsync(
        string message,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("OpenAI API key is not configured.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/responses")
        {
            Content = JsonContent.Create(new OpenAiCreateResponseRequest(
                Model,
                message,
                MaxOutputTokens,
                Store: false))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            response.Headers.TryGetValues("x-request-id", out var requestIds);
            _logger.LogWarning(
                "OpenAI Responses API returned HTTP {StatusCode}; request ID {RequestId}.",
                (int)response.StatusCode,
                requestIds?.FirstOrDefault() ?? "unavailable");
            throw new OpenAiUpstreamException((int)response.StatusCode);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var responseDocument = await JsonDocument.ParseAsync(
            responseStream,
            cancellationToken: cancellationToken);

        return ParseResponse(responseDocument.RootElement);
    }

    private OpenAiResponseResult ParseResponse(JsonElement root)
    {
        var outputText = new StringBuilder();
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var outputItem in output.EnumerateArray())
            {
                if (!outputItem.TryGetProperty("type", out var itemType) ||
                    itemType.GetString() != "message" ||
                    !outputItem.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var contentItem in content.EnumerateArray())
                {
                    if (!contentItem.TryGetProperty("type", out var contentType) ||
                        contentType.GetString() != "output_text" ||
                        !contentItem.TryGetProperty("text", out var text) ||
                        text.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    if (outputText.Length > 0)
                        outputText.AppendLine();
                    outputText.Append(text.GetString());
                }
            }
        }

        if (outputText.Length == 0)
        {
            _logger.LogWarning("OpenAI Responses API returned no output text.");
            throw new OpenAiUpstreamException(502);
        }

        var returnedModel = root.TryGetProperty("model", out var model) &&
                            model.ValueKind == JsonValueKind.String
            ? model.GetString() ?? Model
            : Model;

        return new OpenAiResponseResult(
            outputText.ToString(),
            returnedModel,
            TryReadUsage(root));
    }

    private static AiChatTokenUsage? TryReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object ||
            !TryGetInt32(usage, "input_tokens", out var inputTokens) ||
            !TryGetInt32(usage, "output_tokens", out var outputTokens) ||
            !TryGetInt32(usage, "total_tokens", out var totalTokens))
        {
            return null;
        }

        return new AiChatTokenUsage(inputTokens, outputTokens, totalTokens);
    }

    private static bool TryGetInt32(JsonElement parent, string name, out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var property) && property.TryGetInt32(out value);
    }
}

public sealed record OpenAiResponseResult(
    string Message,
    string Model,
    AiChatTokenUsage? Usage);

public sealed class OpenAiUpstreamException : Exception
{
    public OpenAiUpstreamException(int statusCode)
        : base("The OpenAI Responses API request failed.")
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

public sealed record OpenAiCreateResponseRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("max_output_tokens")] int MaxOutputTokens,
    [property: JsonPropertyName("store")] bool Store);
