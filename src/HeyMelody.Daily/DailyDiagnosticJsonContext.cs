using System.Text.Json.Serialization;

namespace HeyMelodyWindows.Daily;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(DailyApplyResult))]
[JsonSerializable(typeof(EqualizerEdit))]
[JsonSerializable(typeof(DailySnapshot))]
[JsonSerializable(typeof(DailyClient.CodecRecoveryIdentity))]
internal partial class DailyDiagnosticJsonContext : JsonSerializerContext;
