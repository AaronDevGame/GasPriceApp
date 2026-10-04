using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

public static class DoeLocationMatcher
{
    // Philippine provinces by DOE's five report sections; include legacy Maguindanao
    // for older imported rows. Keep province identity separate from city identity.
    private static readonly Dictionary<string, string> ProvinceSections = new(StringComparer.Ordinal)
    {
        ["METRO MANILA"] = "ncr-pump-prices",
        ["ABRA"] = "north-luzon-pump-prices", ["APAYAO"] = "north-luzon-pump-prices",
        ["BENGUET"] = "north-luzon-pump-prices", ["IFUGAO"] = "north-luzon-pump-prices",
        ["KALINGA"] = "north-luzon-pump-prices", ["MOUNTAIN PROVINCE"] = "north-luzon-pump-prices",
        ["ILOCOS NORTE"] = "north-luzon-pump-prices", ["ILOCOS SUR"] = "north-luzon-pump-prices",
        ["LA UNION"] = "north-luzon-pump-prices", ["PANGASINAN"] = "north-luzon-pump-prices",
        ["BATANES"] = "north-luzon-pump-prices", ["CAGAYAN"] = "north-luzon-pump-prices",
        ["ISABELA"] = "north-luzon-pump-prices", ["NUEVA VIZCAYA"] = "north-luzon-pump-prices",
        ["QUIRINO"] = "north-luzon-pump-prices", ["AURORA"] = "north-luzon-pump-prices",
        ["BATAAN"] = "north-luzon-pump-prices", ["BULACAN"] = "north-luzon-pump-prices",
        ["NUEVA ECIJA"] = "north-luzon-pump-prices", ["PAMPANGA"] = "north-luzon-pump-prices",
        ["TARLAC"] = "north-luzon-pump-prices", ["ZAMBALES"] = "north-luzon-pump-prices",
        ["CAVITE"] = "south-luzon-pump-prices", ["LAGUNA"] = "south-luzon-pump-prices",
        ["BATANGAS"] = "south-luzon-pump-prices", ["RIZAL"] = "south-luzon-pump-prices",
        ["QUEZON"] = "south-luzon-pump-prices", ["MARINDUQUE"] = "south-luzon-pump-prices",
        ["OCCIDENTAL MINDORO"] = "south-luzon-pump-prices",
        ["ORIENTAL MINDORO"] = "south-luzon-pump-prices",
        ["PALAWAN"] = "south-luzon-pump-prices", ["ROMBLON"] = "south-luzon-pump-prices",
        ["ALBAY"] = "south-luzon-pump-prices", ["CAMARINES NORTE"] = "south-luzon-pump-prices",
        ["CAMARINES SUR"] = "south-luzon-pump-prices", ["CATANDUANES"] = "south-luzon-pump-prices",
        ["MASBATE"] = "south-luzon-pump-prices", ["SORSOGON"] = "south-luzon-pump-prices",
        ["AKLAN"] = "visayas-pump-prices", ["ANTIQUE"] = "visayas-pump-prices",
        ["CAPIZ"] = "visayas-pump-prices", ["GUIMARAS"] = "visayas-pump-prices",
        ["ILOILO"] = "visayas-pump-prices", ["NEGROS OCCIDENTAL"] = "visayas-pump-prices",
        ["NEGROS ORIENTAL"] = "visayas-pump-prices", ["SIQUIJOR"] = "visayas-pump-prices",
        ["BOHOL"] = "visayas-pump-prices", ["CEBU"] = "visayas-pump-prices",
        ["BILIRAN"] = "visayas-pump-prices", ["EASTERN SAMAR"] = "visayas-pump-prices",
        ["LEYTE"] = "visayas-pump-prices", ["NORTHERN SAMAR"] = "visayas-pump-prices",
        ["SAMAR"] = "visayas-pump-prices", ["SOUTHERN LEYTE"] = "visayas-pump-prices",
        ["ZAMBOANGA DEL NORTE"] = "mindanao-pump-prices",
        ["ZAMBOANGA DEL SUR"] = "mindanao-pump-prices",
        ["ZAMBOANGA SIBUGAY"] = "mindanao-pump-prices",
        ["BUKIDNON"] = "mindanao-pump-prices", ["CAMIGUIN"] = "mindanao-pump-prices",
        ["LANAO DEL NORTE"] = "mindanao-pump-prices",
        ["MISAMIS OCCIDENTAL"] = "mindanao-pump-prices",
        ["MISAMIS ORIENTAL"] = "mindanao-pump-prices",
        ["DAVAO DE ORO"] = "mindanao-pump-prices",
        ["DAVAO DEL NORTE"] = "mindanao-pump-prices",
        ["DAVAO DEL SUR"] = "mindanao-pump-prices",
        ["DAVAO OCCIDENTAL"] = "mindanao-pump-prices",
        ["DAVAO ORIENTAL"] = "mindanao-pump-prices",
        ["COTABATO"] = "mindanao-pump-prices", ["SARANGANI"] = "mindanao-pump-prices",
        ["SOUTH COTABATO"] = "mindanao-pump-prices",
        ["SULTAN KUDARAT"] = "mindanao-pump-prices",
        ["AGUSAN DEL NORTE"] = "mindanao-pump-prices",
        ["AGUSAN DEL SUR"] = "mindanao-pump-prices",
        ["DINAGAT ISLANDS"] = "mindanao-pump-prices",
        ["SURIGAO DEL NORTE"] = "mindanao-pump-prices",
        ["SURIGAO DEL SUR"] = "mindanao-pump-prices",
        ["BASILAN"] = "mindanao-pump-prices", ["LANAO DEL SUR"] = "mindanao-pump-prices",
        ["MAGUINDANAO DEL NORTE"] = "mindanao-pump-prices",
        ["MAGUINDANAO DEL SUR"] = "mindanao-pump-prices",
        ["MAGUINDANAO"] = "mindanao-pump-prices",
        ["SULU"] = "mindanao-pump-prices", ["TAWI TAWI"] = "mindanao-pump-prices"
    };

    public static string Normalize(string value)
    {
        var withoutMarks = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                withoutMarks.Append(char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : ' ');
        return Regex.Replace(withoutMarks.ToString(), @"\s+", " ").Trim();
    }

    public static string Province(string value)
    {
        var name = Normalize(value);
        if (name.StartsWith("PROVINCE OF ", StringComparison.Ordinal)) name = name[12..];
        if (name != "MOUNTAIN PROVINCE" && name.EndsWith(" PROVINCE", StringComparison.Ordinal))
            name = name[..^9];
        return name switch
        {
            "NCR" or "NATIONAL CAPITAL REGION" or "NATIONAL CAPITAL DISTRICT" => "METRO MANILA",
            "MINDORO OCCIDENTAL" => "OCCIDENTAL MINDORO",
            "MINDORO ORIENTAL" => "ORIENTAL MINDORO",
            "NORTH COTABATO" => "COTABATO",
            "COMPOSTELA VALLEY" => "DAVAO DE ORO",
            "WESTERN SAMAR" => "SAMAR",
            "DINAGAT" => "DINAGAT ISLANDS",
            _ => name
        };
    }

    public static string? SectionForProvince(string? province) =>
        province is not null && ProvinceSections.TryGetValue(Province(province), out var section)
            ? section : null;

    public static bool ProvinceEquals(string left, string right) => Province(left) == Province(right);

    public static string[] ProvinceKeyCandidates(string province)
    {
        var canonical = Province(province);
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            DoeFuelPriceImporter.Normalize(province), canonical,
            "PROVINCE OF " + canonical, canonical + " PROVINCE"
        };
        if (canonical == "METRO MANILA")
            names.UnionWith(["NCR", "NATIONAL CAPITAL REGION", "NATIONAL CAPITAL DISTRICT"]);
        if (canonical == "OCCIDENTAL MINDORO") names.Add("MINDORO OCCIDENTAL");
        if (canonical == "ORIENTAL MINDORO") names.Add("MINDORO ORIENTAL");
        if (canonical == "COTABATO") names.Add("NORTH COTABATO");
        if (canonical == "DAVAO DE ORO") names.Add("COMPOSTELA VALLEY");
        if (canonical == "SAMAR") names.Add("WESTERN SAMAR");
        if (canonical == "DINAGAT ISLANDS") names.Add("DINAGAT");
        if (canonical == "TAWI TAWI") names.Add("TAWI-TAWI");
        return names.ToArray();
    }

    public static string? SectionForRegion(string? region)
    {
        if (region is null) return null;
        var name = Normalize(region);
        if (name is "NCR" or "METRO MANILA" or "NATIONAL CAPITAL REGION" or
            "NATIONAL CAPITAL DISTRICT") return "ncr-pump-prices";
        if (name is "CAR" or "CORDILLERA ADMINISTRATIVE REGION" or "ILOCOS REGION" or
            "CAGAYAN VALLEY" or "CENTRAL LUZON" or "REGION I" or "REGION II" or "REGION III" ||
            name.Contains("CORDILLERA", StringComparison.Ordinal) ||
            name.Contains("ILOCOS", StringComparison.Ordinal) ||
            name.Contains("CAGAYAN VALLEY", StringComparison.Ordinal) ||
            name.Contains("CENTRAL LUZON", StringComparison.Ordinal))
            return "north-luzon-pump-prices";
        if (name is "CALABARZON" or "REGION IV A" or "MIMAROPA" or "REGION IV B" or
            "BICOL" or "BICOL REGION" or "REGION V" ||
            name.Contains("CALABARZON", StringComparison.Ordinal) ||
            name.Contains("MIMAROPA", StringComparison.Ordinal) ||
            name.Contains("BICOL", StringComparison.Ordinal)) return "south-luzon-pump-prices";
        if (name is "WESTERN VISAYAS" or "CENTRAL VISAYAS" or "EASTERN VISAYAS" or
            "NEGROS ISLAND REGION" or "NIR" or "REGION VI" or "REGION VII" or "REGION VIII" ||
            name.Contains("VISAYAS", StringComparison.Ordinal) ||
            name.Contains("NEGROS ISLAND", StringComparison.Ordinal))
            return "visayas-pump-prices";
        if (name is "ZAMBOANGA PENINSULA" or "NORTHERN MINDANAO" or "DAVAO REGION" or
            "SOCCSKSARGEN" or "CARAGA" or "BARMM" or
            "BANGSAMORO AUTONOMOUS REGION IN MUSLIM MINDANAO" or
            "REGION IX" or "REGION X" or "REGION XI" or "REGION XII" or "REGION XIII" ||
            name.Contains("MINDANAO", StringComparison.Ordinal) ||
            name.Contains("BANGSAMORO", StringComparison.Ordinal) ||
            name.Contains("SOCCSKSARGEN", StringComparison.Ordinal) ||
            name.Contains("CARAGA", StringComparison.Ordinal) ||
            name.Contains("BARMM", StringComparison.Ordinal) ||
            name.Contains("DAVAO REGION", StringComparison.Ordinal) ||
            name.Contains("ZAMBOANGA PENINSULA", StringComparison.Ordinal))
            return "mindanao-pump-prices";
        return null;
    }

    public static bool RegionConsistent(string? region, string? province)
    {
        var regionSection = SectionForRegion(region);
        var provinceSection = SectionForProvince(province);
        if (regionSection is not null && provinceSection is not null && regionSection != provinceSection)
            return false;
        if (regionSection == "south-luzon-pump-prices" && provinceSection == regionSection)
        {
            var name = Normalize(region!);
            var subdivision = name.Contains("CALABARZON", StringComparison.Ordinal) || name == "REGION IV A"
                ? "Calabarzon"
                : name.Contains("MIMAROPA", StringComparison.Ordinal) || name == "REGION IV B"
                    ? "Mimaropa"
                    : name.Contains("BICOL", StringComparison.Ordinal) || name == "REGION V"
                        ? "Bicol" : null;
            if (subdivision is not null && subdivision != SouthSubdivision(province)) return false;
        }
        return true;
    }

    public static string? SouthSubdivision(string? province)
    {
        var name = province is null ? "" : Province(province);
        if (name is "CAVITE" or "LAGUNA" or "BATANGAS" or "RIZAL" or "QUEZON") return "Calabarzon";
        if (name is "MARINDUQUE" or "OCCIDENTAL MINDORO" or "ORIENTAL MINDORO" or
            "PALAWAN" or "ROMBLON") return "Mimaropa";
        if (name is "ALBAY" or "CAMARINES NORTE" or "CAMARINES SUR" or "CATANDUANES" or
            "MASBATE" or "SORSOGON") return "Bicol";
        return null;
    }

    public static bool CityEquals(string left, string right) => CityBase(left) == CityBase(right);

    public static IReadOnlyList<DoeFuelPrice> MatchCity(
        IEnumerable<DoeFuelPrice> rows, string city, string province, string? region = null)
    {
        if (!RegionConsistent(region, province)) return [];
        var aliases = rows.Where(row =>
            ProvinceEquals(string.IsNullOrWhiteSpace(row.Province) ? row.ProvinceKey : row.Province,
                province) &&
            RegionConsistent(row.Region, province) && CityEquals(RowCity(row), city)).ToArray();
        if (aliases.Length == 0) return [];
        var latestWeek = aliases.Max(row => row.WeekStart);
        var exactName = Normalize(city);
        return aliases.Where(row => row.WeekStart == latestWeek)
            .OrderByDescending(row => Normalize(RowCity(row)) == exactName)
            .ThenByDescending(row => row.FetchedAtUtc)
            .GroupBy(row => Normalize(row.OilCompany) + "|" + Normalize(row.FuelGrade))
            .Select(group => group.First()).ToArray();
    }

    private static string RowCity(DoeFuelPrice row) =>
        string.IsNullOrWhiteSpace(row.City) ? row.CityKey : row.City;

    private static string CityBase(string name)
    {
        var normalized = Normalize(name);
        if (normalized.StartsWith("CITY OF ", StringComparison.Ordinal)) return normalized[8..];
        if (normalized.EndsWith(" CITY", StringComparison.Ordinal)) return normalized[..^5];
        if (normalized.EndsWith(" CTY", StringComparison.Ordinal)) return normalized[..^4];
        return normalized;
    }
}
