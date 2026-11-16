using System.Text.Json.Serialization;

namespace HeyMelodyWindows.Calibration;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(CalibrationSnapshot))]
internal partial class CalibrationDiagnosticJsonContext : JsonSerializerContext;
