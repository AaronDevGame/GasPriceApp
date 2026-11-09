using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record FuelMapCity(string Id, string Name, string Level, string? CityClass,
    string RegionId, string RegionName, string? ProvinceId, string GroupId, string GroupName,
    string GroupType, double[] Bbox, double[] LabelPoint, string FileId);
public sealed record FuelMapFile(string Id, double[] Bbox, int Bytes, int GzipBytes,
    string Sha256, string[] CityIds);
public sealed record FuelMapIndex(string Version, string CountryCode, string PsgcVintage,
    string DefaultCityId, double[] Bbox, string Attribution, string LicenseUrl,
    FuelMapCity[] Cities, FuelMapFile[] Files);

// Immutable packaged PH data only. No runtime downloads, path-based file access, or geocoding.
public sealed class FuelMapGeography
{
    public const string Version = "ph-2026q3-v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public FuelMapIndex Index { get; }
    public IReadOnlyDictionary<string, FuelMapCity> Cities { get; }
    public FuelMapAsset IndexAsset { get; }
    private readonly Dictionary<string, FuelMapAsset> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<FuelMapCity>> _matches = new(StringComparer.Ordinal);

    public FuelMapGeography(IHostEnvironment environment)
    {
        var root = Path.Combine(environment.ContentRootPath, "Geography", Version);
        var bytes = File.ReadAllBytes(Path.Combine(root, "index.json"));
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "manifest.json")));
        if (Hash(bytes) != manifest.RootElement.GetProperty("indexSha256").GetString())
            throw new InvalidDataException("Fuel map index checksum mismatch.");
        Index = JsonSerializer.Deserialize<FuelMapIndex>(bytes, Json)
            ?? throw new InvalidDataException("Missing fuel map index.");
        if (Index.Version != Version || Index.CountryCode != "PH" || Index.Cities.Length != 1642)
            throw new InvalidDataException("Unexpected fuel map geography version or coverage.");
        Cities = Index.Cities.ToDictionary(city => city.Id, StringComparer.Ordinal);
        IndexAsset = FuelMapAsset.Create(bytes, "fuel_map_geography");
        foreach (var file in Index.Files)
        {
            if (!Regex.IsMatch(file.Id, "^[0-9]{10}-[0-9]{2}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Invalid packaged boundary file ID.");
            var data = File.ReadAllBytes(Path.Combine(root, file.Id + ".geojson"));
            if (Hash(data) != file.Sha256 || data.Length != file.Bytes)
                throw new InvalidDataException("Fuel map boundary checksum mismatch.");
            _files.Add(file.Id, FuelMapAsset.Create(data, "fuel_map_boundary"));
        }
        foreach (var city in Cities.Values)
        {
            // The dataset's group is geographic storage, not administrative province membership.
            var province = city.RegionId == "1300000000" ? "Metro Manila" : city.GroupName;
            var provinces = new List<string> { province };
            // DOE reports can retain the pre-split Maguindanao name. City + province matching
            // still has to resolve uniquely; never spread a province row over these cities.
            if (province is "Maguindanao del Norte" or "Maguindanao del Sur") provinces.Add("Maguindanao");
            foreach (var name in provinces)
            {
                var matchKey = Key(city.Name, name);
                if (!_matches.TryGetValue(matchKey, out var matches)) _matches[matchKey] = matches = [];
                matches.Add(city);
            }
        }
    }

    public FuelMapAsset? Boundary(string fileId) => _files.GetValueOrDefault(fileId);

    // Resolve the complete catalog, not just requested cities: an ambiguous name must never
    // appear unique because its competing municipality is outside the current viewport.
    public FuelMapCity? Resolve(DoeFuelPrice row)
    {
        var name = string.IsNullOrWhiteSpace(row.City) ? row.CityKey : row.City;
        if (string.IsNullOrWhiteSpace(name)) return null;
        var province = string.IsNullOrWhiteSpace(row.Province) ? row.ProvinceKey : row.Province;
        if (!DoeLocationMatcher.RegionConsistent(row.Region, province)) return null;
        return _matches.TryGetValue(Key(name, province), out var matches) && matches.Count == 1
            ? matches[0] : null;
    }

    private static string Key(string city, string province) =>
        DoeLocationMatcher.Province(province) + "|" + DoeLocationMatcher.CanonicalCity(city);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

public sealed record FuelMapAsset(byte[] Bytes, byte[] Gzip, string ETag, string GzipETag)
{
    public static FuelMapAsset Create(byte[] data, string message)
    {
        using var document = JsonDocument.Parse(data);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ApiResponse<JsonElement>
        {
            // Asset date is the fixed upstream build date for this version, not process startup.
            Code = ErrorCodes.Ok, Message = message, Date = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc),
            Data = document.RootElement
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(bytes);
        var compressed = stream.ToArray();
        string Tag(byte[] value) => "\"" + Convert.ToHexStringLower(SHA256.HashData(value)) + "\"";
        return new(bytes, compressed, Tag(bytes), Tag(compressed));
    }
}
