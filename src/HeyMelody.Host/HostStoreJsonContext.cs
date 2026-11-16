using System.Text.Json.Serialization;

namespace HeyMelodyWindows.Host;

// Cache formats keep their original camel-case names and explicit null fields.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeviceWhitelistStore.WhitelistDocument), TypeInfoPropertyName = "WhitelistDocument")]
[JsonSerializable(typeof(DeviceAppearanceStore.Document), TypeInfoPropertyName = "AppearanceDocument")]
[JsonSerializable(typeof(DeviceAppearanceStore.Entry), TypeInfoPropertyName = "AppearanceEntry")]
[JsonSerializable(typeof(DeviceAppearanceStore.Entry[]), TypeInfoPropertyName = "AppearanceEntries")]
[JsonSerializable(typeof(FindHeadphonesStore.Document), TypeInfoPropertyName = "LocationDocument")]
[JsonSerializable(typeof(FindHeadphonesStore.Entry), TypeInfoPropertyName = "LocationEntry")]
[JsonSerializable(typeof(FindHeadphonesStore.Entry[]), TypeInfoPropertyName = "LocationEntries")]
[JsonSerializable(typeof(DeviceStateStore.Document), TypeInfoPropertyName = "StateDocument")]
[JsonSerializable(typeof(DeviceStateStore.Entry), TypeInfoPropertyName = "StateEntry")]
[JsonSerializable(typeof(DeviceStateStore.Entry[]), TypeInfoPropertyName = "StateEntries")]
[JsonSerializable(typeof(DeviceStateStore.LegacyDocument), TypeInfoPropertyName = "LegacyStateDocument")]
[JsonSerializable(typeof(DevicePresentation))]
internal partial class HostStoreJsonContext : JsonSerializerContext;
