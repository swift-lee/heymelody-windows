using System.Text.Json.Serialization;

namespace HeyMelodyWindows.Control;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(HiResWorkflowOptions))]
internal partial class ControlDiagnosticJsonContext : JsonSerializerContext;
