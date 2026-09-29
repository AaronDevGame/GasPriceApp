using System.Text.Json;

internal static class FuelPriceSourcePolicy
{
    public static bool HasExcludedSource(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("sources", out var sources) ||
            sources.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object)
                continue;

            if (source.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String &&
                IsExcludedName(name.GetString()!))
                return true;

            if (source.TryGetProperty("url", out var url) &&
                url.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) &&
                uri.Host.Split('.').Any(IsExcludedName))
                return true;
        }

        return false;
    }

    private static bool IsExcludedName(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Contains("zigwheels", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("zigzagwheels", StringComparison.OrdinalIgnoreCase);
    }
}
