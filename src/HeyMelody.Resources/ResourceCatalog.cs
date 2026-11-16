using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HeyMelodyWindows.Resources;

public sealed record ResourcePreferences
{
    public bool UseOnlineResources { get; init; } = true;
    public string HeyMelodyKey { get; init; } = "";
    public string CacheEncryptionKey { get; init; } = "";
}

public sealed record ResourceDeviceProfile(string ProductId, string Name, string Brand, string Type,
    string? Uuid, bool SupportSpp, JsonElement Raw)
{
    public JsonElement Functions => Raw.TryGetProperty("function", out var value) ? value : default;
    public bool CanUseControlTransport => SupportSpp && Guid.TryParse(Uuid, out var service) && service != Guid.Empty;
}

/// <summary>An immutable HeyMelody snapshot. Raw retains unknown vendor fields for future codecs.</summary>
public sealed class ResourceCatalog
{
    public int ContentVersion { get; }
    public bool IsOnline { get; }
    public string Sha256 { get; }
    public JsonElement Raw { get; }
    public IReadOnlyDictionary<string, ResourceDeviceProfile> Profiles { get; }
    /// <summary>Exact response bytes, kept only for online snapshots so a damaged cache can be rewritten without a download. Bundled and worker-parsed catalogs hold null.</summary>
    internal byte[]? OriginalBytes { get; }

    private ResourceCatalog(int version, bool online, string hash, JsonElement raw,
        Dictionary<string, ResourceDeviceProfile> profiles, byte[]? originalBytes)
    { ContentVersion = version; IsOnline = online; Sha256 = hash; Raw = raw; Profiles = new ReadOnlyDictionary<string, ResourceDeviceProfile>(profiles); OriginalBytes = originalBytes; }

    public ResourceDeviceProfile? Find(string? productId) => productId is not null && Profiles.TryGetValue(productId, out var value) ? value : null;

    public static ResourceCatalog Parse(ReadOnlySpan<byte> utf8, bool online, int? bundledVersion = null)
    {
        if (utf8.Length > ResourceLimits.ConfigBytes) throw new InvalidDataException("Configuration exceeds size limit.");
        using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 96 });
        ResourceJson.ValidateUniqueProperties(document.RootElement);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Configuration must be an object.");
        var content = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
        foreach (var (normal, compat) in new[] { ("versionCode", "compatVersionCode"), ("whiteList", "compatWhiteList"), ("caseNumSite", "compatCaseNumSite"), ("appMarket", "compatAppMarket") })
        {
            if (content[compat] is not null) content[normal] = content[compat]!.DeepClone();
            content.Remove(compat);
        }
        int version = bundledVersion ?? Integer(content["versionCode"]);
        if (version < 0) throw new InvalidDataException("Invalid content version.");
        // Android supplies this override outside the offline JSON. Include the
        // resolved version in exported snapshots so another process can parse them.
        content["versionCode"] = version;
        if (content["whiteList"] is not JsonArray rows) throw new InvalidDataException("Configuration has no whiteList array.");
        var profiles = new Dictionary<string, ResourceDeviceProfile>(StringComparer.Ordinal);
        foreach (var item in rows)
        {
            if (item is not JsonObject parent) throw new InvalidDataException("Invalid device row.");
            Add(parent);
            if (parent["children"] is not JsonArray children) continue;
            foreach (var childItem in children)
            {
                if (childItem is not JsonObject child || string.IsNullOrEmpty(Text(child["id"]))) continue;
                var inherited = parent.DeepClone().AsObject();
                foreach (var field in new[] { "id", "name", "brand", "uuid" })
                    if (!string.IsNullOrEmpty(Text(child[field]))) inherited[field] = child[field]!.DeepClone();
                inherited.Remove("children");
                if (inherited["function"] is JsonObject features) features["hideFromUIList"] = 1;
                Add(inherited);
            }
        }
        return new(version, online, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(utf8)),
            JsonSerializer.SerializeToElement(content, ResourceWireJsonContext.Default.JsonObject), profiles, online ? utf8.ToArray() : null);

        void Add(JsonObject row)
        {
            string id = Text(row["id"]), brand = Text(row["brand"]), type = Text(row["type"]);
            if (!Regex.IsMatch(id, "^[0-9A-F]{6}$")) throw new InvalidDataException("Invalid product identifier.");
            if (!new[] { "oppo", "oneplus", "realme" }.Contains(brand, StringComparer.OrdinalIgnoreCase)) return;
            if (type.StartsWith("S", StringComparison.OrdinalIgnoreCase) || type.Contains("speaker", StringComparison.OrdinalIgnoreCase)) return;
            bool spp = row["supportSpp"] is JsonValue value && value.TryGetValue<bool>(out var supported) && supported;
            var profile = new ResourceDeviceProfile(id, Text(row["name"]), brand, type, Text(row["uuid"]) is { Length: > 0 } uuid ? uuid : null,
                spp, JsonSerializer.SerializeToElement(row, ResourceWireJsonContext.Default.JsonObject));
            if (!profiles.TryAdd(id, profile)) throw new InvalidDataException("Duplicate product identifier.");
        }
    }

    public static ResourceCatalog Select(ResourceCatalog bundled, ResourceCatalog? cached)
    {
        return cached is not null && cached.ContentVersion > bundled.ContentVersion ? cached : bundled;
    }

    private static int Integer(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out int n) && n >= 0 ? n : throw new InvalidDataException("Invalid content version.");
    private static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? text : "";
}

internal static class ResourceJson
{
    internal static JsonSerializerOptions Options => ResourceJsonContext.Default.Options;
    internal static void ValidateUniqueProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            { if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate JSON member."); ValidateUniqueProperties(property.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) ValidateUniqueProperties(item);
    }
}

public static class ResourceLimits
{
    public const int MetadataBytes = 1024 * 1024;
    public const int ConfigBytes = 8 * 1024 * 1024;
    public const int ArchiveBytes = 64 * 1024 * 1024;
    public const int ExpandedBytes = 256 * 1024 * 1024;
    public const int MemberBytes = 64 * 1024 * 1024;
    public const int Members = 2048;
}
