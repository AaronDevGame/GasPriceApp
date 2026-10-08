using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

public sealed class FuelNews
{
    public Guid Id { get; set; }
    public string ImportKey { get; set; } = "";
    public string TopicKey { get; set; } = "";
    public string Category { get; set; } = "";
    public string Status { get; set; } = "";
    public string ContentJson { get; set; } = "{}";
    public string ContentHash { get; set; } = "";
    public int Revision { get; set; }
    public DateTime PublishedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public Guid? SupersededById { get; set; }
}

public sealed class FuelNewsRevision
{
    public Guid NewsId { get; set; }
    public int Revision { get; set; }
    public string ContentJson { get; set; } = "{}";
    public DateTime SavedAtUtc { get; set; }
}

public sealed record FuelNewsChange(string Fuel, string? OilCompany,
    decimal MinChangePerLiter, decimal MaxChangePerLiter, string Status);
public sealed record FuelNewsSource(string Name, string Url, DateTime PublishedAtUtc);
public sealed record FuelNewsContent(string ImportKey, string TopicKey, string Category,
    string Status, string Title, string Summary, string Body,
    DateOnly? EffectiveDatePhilippines, DateTime? EffectiveAtUtc,
    DateTime ExpiresAtUtc, FuelNewsChange[] Adjustments, FuelNewsSource[] Sources);
public sealed record FuelNewsImport(FuelNewsContent Article, int? ExpectedRevision);
public sealed record FuelNewsView(Guid Id, int Revision, DateTime PublishedAtUtc,
    DateTime UpdatedAtUtc, Guid? SupersededById, bool IsExpired, FuelNewsContent Article);
public sealed record FuelNewsPage(IReadOnlyList<FuelNewsView> Items, string? NextCursor);
public sealed record FuelNewsImportResult(string Status, FuelNewsView Item);

public static partial class FuelNewsValidation
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 8,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { info =>
            {
                if (info.Kind == JsonTypeInfoKind.Object)
                    foreach (var property in info.Properties) property.IsRequired = true;
            } }
        }
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{2,99}$")]
    private static partial Regex KeyPattern();

    public static string? Validate(FuelNewsImport? request, DateTime now)
    {
        if (request?.Article is not { } a) return "Provide article and expectedRevision.";
        if (request.ExpectedRevision is < 1) return "expectedRevision must be null or a positive integer.";
        if (a.ImportKey is null || a.TopicKey is null ||
            !KeyPattern().IsMatch(a.ImportKey) || !KeyPattern().IsMatch(a.TopicKey))
            return "importKey and topicKey must be 3–100 lowercase letters, digits or hyphens.";
        if (a.Category is not ("adjustment" or "general") ||
            a.Status is not ("forecast" or "confirmed" or "general") ||
            (a.Category == "general") != (a.Status == "general"))
            return "Use adjustment with forecast/confirmed, or general with general status.";
        if (!Text(a.Title, 10, 160) || !Text(a.Summary, 20, 600) || !Text(a.Body, 40, 6000))
            return "Provide plain-text title (10–160), summary (20–600), and body (40–6000 characters).";
        if (a.ExpiresAtUtc.Kind != DateTimeKind.Utc || a.ExpiresAtUtc <= now || a.ExpiresAtUtc > now.AddDays(30))
            return "expiresAtUtc must be a future UTC timestamp within 30 days.";
        if (a.EffectiveAtUtc is { } effective &&
            (effective.Kind != DateTimeKind.Utc || a.EffectiveDatePhilippines is null ||
             DateOnly.FromDateTime(effective.AddHours(8)) != a.EffectiveDatePhilippines))
            return "effectiveAtUtc must be UTC and match effectiveDatePhilippines.";
        if (a.Adjustments is null || a.Adjustments.Length > 30 ||
            (a.Category == "general" && (a.Adjustments.Length != 0 || a.EffectiveDatePhilippines is not null || a.EffectiveAtUtc is not null)))
            return "General news has no adjustments or effective date; at most 30 adjustment rows are allowed.";
        if (a.Category == "adjustment")
        {
            var today = DateOnly.FromDateTime(now.AddHours(8));
            if (a.EffectiveDatePhilippines is not { } date || date < today.AddDays(-7) || date > today.AddDays(14) || a.Adjustments.Length == 0)
                return "Adjustment news needs changes and an effective date between 7 days ago and 14 days ahead.";
            if (a.Status == "forecast" && (date < today || a.ExpiresAtUtc > date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-8)))
                return "Forecasts must expire by the end of their effective Philippine date and cannot concern past dates.";
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in a.Adjustments)
        {
            if (change is null || change.Fuel is not ("gasoline" or "diesel" or "kerosene") ||
                change.Status is not ("forecast" or "confirmed") ||
                (a.Status == "confirmed" && change.Status != "confirmed") ||
                (change.OilCompany is not null && !Text(change.OilCompany, 2, 100)) ||
                !seen.Add(change.Fuel + "|" + change.OilCompany))
                return "Use unique fuel/company rows with forecast or confirmed status.";
            if (change.MinChangePerLiter < -30 || change.MaxChangePerLiter > 30 ||
                change.MinChangePerLiter > change.MaxChangePerLiter ||
                decimal.Round(change.MinChangePerLiter, 2) != change.MinChangePerLiter ||
                decimal.Round(change.MaxChangePerLiter, 2) != change.MaxChangePerLiter ||
                (change.Status == "confirmed" && change.MinChangePerLiter != change.MaxChangePerLiter))
                return "Changes must be ordered PHP/liter amounts from -30 to 30, with up to two decimals; confirmed changes are exact.";
        }
        if (a.Sources is null || a.Sources.Length is < 1 or > 10)
            return "Provide 1–10 dated source links.";
        seen.Clear();
        foreach (var source in a.Sources)
        {
            if (source is null || !Text(source.Name, 2, 120) || source.Url is null || source.Url.Length > 2048 ||
                !Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.UserInfo.Length != 0 || uri.IsLoopback || uri.HostNameType != UriHostNameType.Dns ||
                !uri.Host.Contains('.') || !seen.Add(source.Url) || source.PublishedAtUtc.Kind != DateTimeKind.Utc ||
                source.PublishedAtUtc > now.AddMinutes(5) || source.PublishedAtUtc < now.AddDays(-14))
                return "Sources need unique public HTTPS URLs, names, and UTC publication timestamps from the last 14 days.";
        }
        return null;
    }

    private static bool Text(string? value, int min, int max) => value is not null &&
        value.Length >= min && value.Length <= max && value == value.Trim() &&
        !value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) &&
        !value.Contains('<') && !value.Contains('>');

    // System.Text.Json permits duplicate keys. Reject them at every nesting level.
    public static bool HasDuplicateProperties(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1) ||
            element.EnumerateObject().Any(p => HasDuplicateProperties(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(HasDuplicateProperties),
        _ => false
    };

    public static FuelNewsView View(FuelNews item, DateTime now) => new(item.Id, item.Revision,
        item.PublishedAtUtc, item.UpdatedAtUtc, item.SupersededById, item.ExpiresAtUtc <= now,
        JsonSerializer.Deserialize<FuelNewsContent>(item.ContentJson, Json)!);
}
