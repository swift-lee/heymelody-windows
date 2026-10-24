using System.Text.Json;
using System.Text.Json.Serialization;
using HeyMelody.Diagnostics;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Host;

// Source-generated metadata for every type that crosses RpcWire.Json. General defaults, not Web: the generated writers
// are only used while the runtime options match these settings (naming policy, ignore condition, no converters/encoder).
// `object` and the collection roots cover Reply/SerializeToElement(object) and RequestAsync<List<...>> callers;
// anything not listed (worker-private records, anonymous diagnostics) falls back to reflection in RpcWire.Json.
[JsonSourceGenerationOptions(JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BackendRequest))]
[JsonSerializable(typeof(BackendMessage))]
[JsonSerializable(typeof(BackendEvent))]
[JsonSerializable(typeof(BackendSubscription))]
[JsonSerializable(typeof(DiagnosticFailure))]
[JsonSerializable(typeof(DiagnosticFailure[]))]
[JsonSerializable(typeof(RpcRequest))]
[JsonSerializable(typeof(RpcResponse))]
[JsonSerializable(typeof(UiSnapshot))]
[JsonSerializable(typeof(UiApplyResult))]
[JsonSerializable(typeof(UiAction))]
[JsonSerializable(typeof(UiAncMode))]
[JsonSerializable(typeof(UiCalibrationInput))]
[JsonSerializable(typeof(UiCalibrationState))]
[JsonSerializable(typeof(UiCalibrationSpectrum))]
[JsonSerializable(typeof(UiResourceSettings))]
[JsonSerializable(typeof(UiResourceRedownloadResult))]
[JsonSerializable(typeof(UiDeviceCatalog))]
[JsonSerializable(typeof(UiWhitelistSaveResult))]
[JsonSerializable(typeof(UiArtwork))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(UiDevice[]))]
[JsonSerializable(typeof(List<UiDevice>))]
[JsonSerializable(typeof(UiHearingProfile[]))]
[JsonSerializable(typeof(List<UiHearingProfile>))]
[JsonSerializable(typeof(HearingSelectionArgs))]
[JsonSerializable(typeof(CalibrationCreateArgs))]
[JsonSerializable(typeof(ArtworkAcquireArgs))]
[JsonSerializable(typeof(ArtworkReleaseArgs))]
[JsonSerializable(typeof(ArtworkLeaseReply))]
public partial class RpcJsonContext : JsonSerializerContext;
