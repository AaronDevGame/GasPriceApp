using System.Text.Json;

public sealed record AiChatResponse(
    string Message,
    string Model,
    AiChatTokenUsage? Usage,
    bool UsedWebSearch,
    IReadOnlyList<AiChatSource> Sources);

public sealed record AiChatSource(
    string? Title,
    string Url);

public sealed record AiChatTokenUsage(
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    int TotalTokens);

public static class AiChatEndpoints
{
    private const int MaxMessageLength = 4_000;

    public static void MapAiChatEndpoints(
        this WebApplication app,
        AuthService authService,
        string instanceId)
    {
        app.MapPost(ApiRoutes.AiChat, async (
            HttpRequest request,
            AppDbContext db,
            OpenAiResponsesClient openAi,
            CancellationToken cancellationToken) =>
        {
            var auth = await PlayerAuthentication.AuthenticateAsync(request, db, authService);
            if (!auth.IsValid || auth.Guest is null)
                return auth.IsBadRequest
                    ? ApiResults.BadRequest(auth.Error, instanceId)
                    : ApiResults.Unauthorized(auth.Error, instanceId);

            if (!request.HasJsonContentType())
                return ApiResults.BadRequest("Request body must be JSON.", instanceId);

            JsonDocument requestDocument;
            try
            {
                requestDocument = await JsonDocument.ParseAsync(
                    request.Body,
                    cancellationToken: cancellationToken);
            }
            catch (JsonException)
            {
                return ApiResults.BadRequest("Request body must be valid JSON.", instanceId);
            }

            using var _ = requestDocument;
            if (!TryReadMessage(requestDocument.RootElement, out var message, out var error))
                return ApiResults.BadRequest(error, instanceId);

            if (!openAi.IsConfigured)
                return ApiResults.ServiceUnavailable(
                    "ai_service_not_configured",
                    instanceId,
                    "The server AI integration has not been configured.");

            try
            {
                var response = await openAi.CreateResponseAsync(message, cancellationToken);
                return ApiResults.Ok(
                    new AiChatResponse(
                        response.Message,
                        response.Model,
                        response.Usage,
                        response.UsedWebSearch,
                        response.Sources),
                    "ai_chat_response",
                    instanceId);
            }
            catch (OpenAiUpstreamException ex) when (ex.StatusCode is 401 or 403)
            {
                return ApiResults.ServiceUnavailable(
                    "ai_service_not_configured",
                    instanceId,
                    "The server AI integration could not authenticate with its provider.");
            }
            catch (OpenAiUpstreamException ex) when (ex.StatusCode == 429)
            {
                return ApiResults.ServiceUnavailable(
                    "ai_service_rate_limited",
                    instanceId,
                    "The AI service is temporarily rate limited. Try again later.");
            }
            catch (OpenAiUpstreamException)
            {
                return ApiResults.BadGateway(
                    "ai_service_error",
                    instanceId,
                    "The AI service could not complete the request.");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ApiResults.GatewayTimeout(
                    "ai_service_timeout",
                    instanceId,
                    "The AI service did not respond in time.");
            }
            catch (HttpRequestException)
            {
                return ApiResults.ServiceUnavailable(
                    "ai_service_unavailable",
                    instanceId,
                    "The AI service is temporarily unavailable.");
            }
            catch (JsonException)
            {
                return ApiResults.BadGateway(
                    "ai_invalid_response",
                    instanceId,
                    "The AI service returned an invalid response.");
            }
        });
    }

    private static bool TryReadMessage(
        JsonElement root,
        out string message,
        out string error)
    {
        message = "";
        error = "";

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "AI chat request must be a JSON object.";
            return false;
        }

        var hasMessage = false;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name != "message")
            {
                error = $"Unknown AI chat field '{property.Name}'.";
                return false;
            }

            if (hasMessage)
            {
                error = "Message must be provided only once.";
                return false;
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                error = "Message must be a string.";
                return false;
            }

            message = (property.Value.GetString() ?? "").Trim();
            hasMessage = true;
        }

        if (!hasMessage || message.Length == 0)
        {
            error = "Message is required.";
            return false;
        }

        if (message.Length > MaxMessageLength)
        {
            error = $"Message must not exceed {MaxMessageLength} characters.";
            return false;
        }

        return true;
    }
}
