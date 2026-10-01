using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

public sealed record ExtractedFuelAdjustment(
    [property: JsonPropertyName("oil_company")] string OilCompany,
    [property: JsonPropertyName("effective_date_philippines")] string EffectiveDatePhilippines,
    [property: JsonPropertyName("effective_time_philippines")] string? EffectiveTimePhilippines,
    [property: JsonPropertyName("gasoline_change_per_liter")] decimal? GasolineChangePerLiter,
    [property: JsonPropertyName("diesel_change_per_liter")] decimal? DieselChangePerLiter,
    [property: JsonPropertyName("kerosene_change_per_liter")] decimal? KeroseneChangePerLiter);

public sealed record FuelAdjustmentExtraction(
    [property: JsonPropertyName("week_start")] string WeekStart,
    [property: JsonPropertyName("week_end")] string WeekEnd,
    [property: JsonPropertyName("rows")] IReadOnlyList<ExtractedFuelAdjustment> Rows);

public sealed class FuelAdjustmentImporter
{
    public const string DoePage = "https://doe.gov.ph/data-and-prices/liquid-fuels/retail-pump-prices/price-adjustments";
    private const int MaxPdfBytes = 5 * 1024 * 1024;
    private const string AgentPath = "agents/philippines-fuel-adjustment-extractor.md";
    private static readonly Regex PdfLinks = new(
        "href\\s*=\\s*[\"'](?<url>[^\"']+\\.pdf(?:\\?[^\"']*)?)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly JsonElement ExtractionSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "week_start", "week_end", "rows" },
        properties = new
        {
            week_start = new { type = "string" },
            week_end = new { type = "string" },
            rows = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[]
                    {
                        "oil_company", "effective_date_philippines", "effective_time_philippines",
                        "gasoline_change_per_liter", "diesel_change_per_liter",
                        "kerosene_change_per_liter"
                    },
                    properties = new
                    {
                        oil_company = new { type = "string" },
                        effective_date_philippines = new { type = "string" },
                        effective_time_philippines = new { type = new[] { "string", "null" } },
                        gasoline_change_per_liter = new { type = new[] { "number", "null" } },
                        diesel_change_per_liter = new { type = new[] { "number", "null" } },
                        kerosene_change_per_liter = new { type = new[] { "number", "null" } }
                    }
                }
            }
        }
    });

    private readonly HttpClient _sourceClient;
    private readonly HttpClient _openAiClient;
    private readonly IConfiguration _configuration;
    private readonly string _instructions;

    public FuelAdjustmentImporter(
        HttpClient sourceClient,
        IHttpClientFactory clients,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _sourceClient = sourceClient;
        _openAiClient = clients.CreateClient("fuel-adjustment-openai");
        _configuration = configuration;
        var path = Path.Combine(environment.ContentRootPath, AgentPath);
        _instructions = File.ReadAllText(path);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_configuration["OPENAI_API_KEY"]);

    public async Task<(string SourceUrl, FuelAdjustmentExtraction Extraction)> ImportAsync(
        string? requestedUrl,
        CancellationToken cancellationToken)
    {
        var sourceUrl = requestedUrl is null
            ? await FindLatestUrlAsync(cancellationToken)
            : ValidatePdfUrl(requestedUrl);
        var pdf = await DownloadPdfAsync(sourceUrl, cancellationToken);
        var extraction = await ExtractAsync(pdf, cancellationToken);
        return (sourceUrl, extraction);
    }

    public async Task<IReadOnlyList<string>> FindNoticeUrlsAsync(CancellationToken cancellationToken)
    {
        using var response = await _sourceClient.GetAsync(DoePage, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(cancellationToken));
        var attachmentSection = html.IndexOf(
            "Summary of Prior Notice on Price Adjustments", StringComparison.OrdinalIgnoreCase);
        if (attachmentSection < 0)
            throw new InvalidDataException("DOE price-adjustments page did not contain its notice section.");

        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in PdfLinks.Matches(html[attachmentSection..]))
        {
            if (Uri.TryCreate(new Uri(DoePage), match.Groups["url"].Value, out var uri) &&
                IsAllowedPdfUrl(uri))
                urls.Add(uri.AbsoluteUri);
        }
        if (urls.Count == 0)
            throw new InvalidDataException("No DOE fuel-adjustment PDF was found on the price-adjustments page.");
        return urls.ToArray();
    }

    private async Task<string> FindLatestUrlAsync(CancellationToken cancellationToken)
    {
        return (await FindNoticeUrlsAsync(cancellationToken))[0];
    }

    public static string ValidatePdfUrl(string value)
    {
        if (value.Length > 2048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsAllowedPdfUrl(uri))
            throw new ArgumentException("sourceUrl must be a DOE fuel-adjustment PDF URL.");
        return uri.AbsoluteUri;
    }

    private static bool IsAllowedPdfUrl(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Port != 443)
            return false;
        var host = uri.Host.ToLowerInvariant();
        if (host is not ("doe.gov.ph" or "prod-cms.doe.gov.ph" or
            "legacy.doe.gov.ph" or "d24qbtp4vooyzi.cloudfront.net"))
            return false;
        return uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<byte[]> DownloadPdfAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _sourceClient.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxPdfBytes)
            throw new InvalidDataException("DOE PDF exceeds the import size limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (buffer.Length + count > MaxPdfBytes)
                throw new InvalidDataException("DOE PDF exceeds the import size limit.");
            buffer.Write(chunk, 0, count);
        }

        var bytes = buffer.ToArray();
        if (bytes.Length < 5 || Encoding.ASCII.GetString(bytes, 0, 5) != "%PDF-")
            throw new InvalidDataException("DOE source did not return a PDF.");
        return bytes;
    }

    private async Task<FuelAdjustmentExtraction> ExtractAsync(byte[] pdf, CancellationToken cancellationToken)
    {
        var key = _configuration["OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OPENAI_API_KEY is not configured.");

        var model = _configuration["OPENAI_MODEL"];
        model = string.IsNullOrWhiteSpace(model) ? "gpt-5.6-luna" : model.Trim();

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/responses")
        {
            Content = JsonContent.Create(new
            {
                model,
                instructions = _instructions,
                input = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new
                            {
                                type = "input_file",
                                filename = "doe-fuel-adjustments.pdf",
                                file_data = "data:application/pdf;base64," + Convert.ToBase64String(pdf)
                            },
                            new { type = "input_text", text = "Extract every oil-company price-adjustment row from this DOE notice." }
                        }
                    }
                },
                text = new
                {
                    format = new
                    {
                        type = "json_schema",
                        name = "doe_fuel_adjustments",
                        strict = true,
                        schema = ExtractionSchema
                    }
                },
                max_output_tokens = 4000,
                store = false
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await _openAiClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("OpenAI could not extract the DOE notice.", null, response.StatusCode);

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("status", out var status) ||
            status.GetString() != "completed")
            throw new InvalidDataException("OpenAI did not complete PDF extraction.");

        var text = new StringBuilder();
        if (!document.RootElement.TryGetProperty("output", out var outputs) ||
            outputs.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("OpenAI response did not contain extracted rows.");
        foreach (var output in outputs.EnumerateArray())
        {
            if (!output.TryGetProperty("type", out var type) || type.GetString() != "message" ||
                !output.TryGetProperty("content", out var content))
                continue;
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var partType) && partType.GetString() == "output_text")
                    text.Append(part.GetProperty("text").GetString());
            }
        }

        if (text.Length == 0)
            throw new InvalidDataException("OpenAI response did not contain extracted rows.");
        var extracted = JsonSerializer.Deserialize<FuelAdjustmentExtraction>(text.ToString());
        return extracted ?? throw new InvalidDataException("OpenAI returned no fuel adjustments.");
    }
}
