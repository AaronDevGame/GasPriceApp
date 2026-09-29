using System.Text.Json;

public sealed class FuelPriceSourcePolicy
{
    public const string RelativePath = "agents/excluded-fuel-price-sources.json";
    private readonly List<string> _names = [];
    private readonly List<string> _domains = [];

    public string Instructions { get; }
    public IReadOnlyList<string> BlockedDomains => _domains;

    public FuelPriceSourcePolicy(IHostEnvironment environment)
    {
        var json = File.ReadAllText(Path.Combine(environment.ContentRootPath, RelativePath));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("sources", out var sources) ||
            sources.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Fuel-price exclusions must contain a sources array.");

        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object ||
                !source.TryGetProperty("names", out var names) ||
                names.ValueKind != JsonValueKind.Array || names.GetArrayLength() == 0 ||
                !source.TryGetProperty("domains", out var domains) ||
                domains.ValueKind != JsonValueKind.Array || domains.GetArrayLength() == 0)
                throw new InvalidDataException("Each excluded source requires names and domains arrays.");

            foreach (var name in names.EnumerateArray())
            {
                if (name.ValueKind != JsonValueKind.String ||
                    NormalizeName(name.GetString()!) is not { Length: > 0 } normalized)
                    throw new InvalidDataException("Excluded source names must be nonempty strings.");
                _names.Add(normalized);
            }

            foreach (var domain in domains.EnumerateArray())
            {
                if (domain.ValueKind != JsonValueKind.String ||
                    domain.GetString() is not { Length: > 0 } host ||
                    Uri.CheckHostName(host) != UriHostNameType.Dns || !host.Contains('.'))
                    throw new InvalidDataException("Excluded source domains must be DNS host names.");
                _domains.Add(host.TrimEnd('.'));
            }
        }

        if (_domains.Count > 100)
            throw new InvalidDataException("The web-search tool supports at most 100 blocked domains.");

        Instructions = $"Contents of {RelativePath}:\n{json}";
    }

    public bool HasExcludedSource(JsonElement result)
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
                IsExcludedDomain(uri.Host))
                return true;
        }

        return false;
    }

    private bool IsExcludedName(string name)
        => _names.Any(excluded => NormalizeName(name).Contains(
            excluded, StringComparison.OrdinalIgnoreCase));

    private bool IsExcludedDomain(string host)
        => _domains.Any(domain => host.TrimEnd('.').Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.TrimEnd('.').EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeName(string name)
        => new(name.Where(char.IsLetterOrDigit).ToArray());
}
