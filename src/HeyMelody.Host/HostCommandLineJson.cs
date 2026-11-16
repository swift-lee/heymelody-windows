using System.Text.Json.Serialization;

namespace HeyMelodyWindows.Host;

internal sealed record LocationPermissionCheckResult(string Status, string? Message, int? Hresult);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(LocationPermissionCheckResult))]
internal partial class HostCommandLineJson : JsonSerializerContext;
