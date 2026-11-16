using System.Text.Json.Serialization;
using HeyMelody.Platform;

namespace HeyMelodyWindows.Windows;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BluetoothCandidate[]))]
[JsonSerializable(typeof(IReadOnlyList<RfcommService>), TypeInfoPropertyName = "RfcommServices")]
internal partial class WindowsDiagnosticJsonContext : JsonSerializerContext;
