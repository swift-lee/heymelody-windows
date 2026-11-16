using System.Text.Json.Serialization;
using HeyMelody.Platform;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Host;

// Diagnostic DTO fields previously used the serializer's default PascalCase names, independently of RPC.
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BackendRequest))]
[JsonSerializable(typeof(BluetoothCandidate))]
[JsonSerializable(typeof(UiSnapshot))]
internal partial class HostDiagnosticJsonContext : JsonSerializerContext;
