using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HeyMelody.Diagnostics;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(DiagnosticFailure))]
[JsonSerializable(typeof(DiagnosticSettings))]
[JsonSerializable(typeof(DiagnosticSessionInfo))]
[JsonSerializable(typeof(DiagnosticRuntime.LaunchOwner))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonElement))]
internal partial class DiagnosticJsonContext : JsonSerializerContext;
