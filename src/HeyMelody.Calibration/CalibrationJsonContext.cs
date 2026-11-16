using System.Text.Json.Serialization;

namespace HeyMelodyWindows.Calibration;

// Metadata mode retains null byte[] as JSON null; the fast writer treats a null span as empty base64.
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, WriteIndented = true)]
[JsonSerializable(typeof(CalibrationHistoryEntry[]))]
[JsonSerializable(typeof(CalibrationHistoryEntry))]
[JsonSerializable(typeof(IReadOnlyList<CalibrationHistoryEntry>), TypeInfoPropertyName = "HistoryEntries")]
internal partial class CalibrationJsonContext : JsonSerializerContext;
