using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HeyMelodyWindows.Resources;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(ResourcePreferences))]
[JsonSerializable(typeof(ResourceArtwork[]))]
internal partial class ResourceJsonContext : JsonSerializerContext;

// Vendor request signing depends on compact, insertion-ordered dictionary output.
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(ArtworkPackageMarker))]
internal partial class ResourceWireJsonContext : JsonSerializerContext;

internal sealed record ArtworkPackageMarker(int schemaVersion, string sha256);
