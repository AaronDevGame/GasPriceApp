using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

// Retain only bounded identifiers, never provider messages, request bodies, or credentials.
internal sealed class DoeExtractionRequestException : HttpRequestException
{
    public string? ErrorCode { get; }
    public string? RequestId { get; }
    public bool IsTransient { get; }
    public TimeSpan RetryAfter { get; }

    private DoeExtractionRequestException(HttpStatusCode status, string? code, string? requestId,
        bool transient, TimeSpan retryAfter)
        : base($"DOE extraction request failed (HTTP {(int)status}" +
            (code is null ? "" : $", code {code}") +
            (requestId is null ? "" : $", request ID {requestId}") + ").", null, status)
    {
        ErrorCode = code;
        RequestId = requestId;
        IsTransient = transient;
        RetryAfter = retryAfter;
    }

    public static async Task<DoeExtractionRequestException> FromResponseAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? code = null;
        var insufficientQuota = false;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[16 * 1024];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken);
                if (read == 0) break;
                count += read;
            }
            using var body = JsonDocument.Parse(buffer.AsMemory(0, count));
            if (body.RootElement.ValueKind == JsonValueKind.Object &&
                body.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                insufficientQuota = error.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String && type.GetString() == "insufficient_quota";
                if (error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var candidate = value.GetString();
                    if (candidate is not null && Regex.IsMatch(candidate, "\\A[a-z][a-z0-9_]{0,79}\\z"))
                        code = candidate;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException)
        {
            // The HTTP status remains useful even if the error body is missing or malformed.
        }
        var requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
        if (requestId is null || !Regex.IsMatch(requestId, "\\Areq_[A-Za-z0-9_-]{1,120}\\z")) requestId = null;
        var retryAfter = response.Headers.RetryAfter?.Delta ??
            (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.Zero;
        if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
        var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict ||
            (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests;
        // Quota, credit, and enforced spending limits require account changes, not retries.
        if (insufficientQuota || code is "insufficient_quota" or "credit_balance_exhausted" or "billing_hard_limit_reached" or
            "organization_spend_limit_exceeded" or "project_spend_limit_exceeded" or "organization_usage_limit_exceeded")
            transient = false;
        return new(response.StatusCode, code, requestId, transient, retryAfter);
    }
}
