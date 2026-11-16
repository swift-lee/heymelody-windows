using System.Text.Json.Serialization;

namespace HeyMelody.Platform;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InstallationSuccess))]
[JsonSerializable(typeof(InstallationFailure))]
internal partial class InstallationJsonContext : JsonSerializerContext;

internal sealed record InstallationSuccess(bool Success, string? Component, string? Version, string RuntimeIdentifier,
    bool ResourceBundles, bool NativeLibrariesLoaded, int NativeBluetoothCalls, int AudioPlaybackCalls);
internal sealed record InstallationFailure(bool Success, string Error, string Message, int NativeBluetoothCalls, int AudioPlaybackCalls);
