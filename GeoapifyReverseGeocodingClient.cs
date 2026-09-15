using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

public sealed record ResolvedFuelLocation(string? City, string Province, string? Region);

public sealed class GeoapifyReverseGeocodingClient
{
    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;

    public GeoapifyReverseGeocodingClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _apiKey = configuration["GEOAPIFY_API_KEY"];
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<ResolvedFuelLocation?> ResolveAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Geoapify API key is not configured.");

        var url = $"v1/geocode/reverse?lat={latitude.ToString(CultureInfo.InvariantCulture)}" +
                  $"&lon={longitude.ToString(CultureInfo.InvariantCulture)}" +
                  $"&format=geojson&apiKey={Uri.EscapeDataString(_apiKey!)}";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(
            cancellationToken: cancellationToken);
        if (document is null ||
            document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("features", out var features) ||
            features.ValueKind != JsonValueKind.Array)
            throw new JsonException("Geoapify response was not a feature collection.");

        if (features.GetArrayLength() == 0)
            return null;

        var feature = features[0];
        if (feature.ValueKind != JsonValueKind.Object ||
            !feature.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
            throw new JsonException("Geoapify feature had no properties.");

        if (!string.Equals(ReadString(properties, "country_code"), "ph", StringComparison.OrdinalIgnoreCase))
            return null;

        var city = ReadString(properties, "city") ??
                   ReadString(properties, "town") ??
                   ReadString(properties, "municipality") ??
                   ReadString(properties, "village");
        var province = ReadString(properties, "state");
        var region = ReadString(properties, "region");

        // Geoapify can identify NCR as a state without returning a region.
        if (IsNcr(province) || (province is null && IsNcr(region)))
        {
            province = "Metro Manila";
            region = "National Capital Region";
        }

        if (string.IsNullOrWhiteSpace(province))
            return null;

        return new ResolvedFuelLocation(city, province, region);
    }

    private static bool IsNcr(string? value)
        => value is not null &&
           (string.Equals(value, "National Capital District", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "National Capital Region", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Metro Manila", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "NCR", StringComparison.OrdinalIgnoreCase));

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        var text = value.GetString()?.Trim();
        return text is { Length: > 0 } && text.Length <= 100 ? text : null;
    }
}
