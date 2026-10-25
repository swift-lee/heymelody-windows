using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using HeyMelodyWindows.App;
using HeyMelody.Platform;

namespace HeyMelodyWindows.Host;

// Host-private wire types for the worker stdio. The generation options equal RpcJsonContext's and the runtime options copy
// RpcWire.Json, so the generated writers apply and the bytes match the reflection output. Everything shared with the
// backend IPC (UiSnapshot and friends) still resolves through RpcJsonContext, which comes first in the resolver chain.
[JsonSourceGenerationOptions(JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WorkerRequest))]
[JsonSerializable(typeof(WorkerMessage))]
[JsonSerializable(typeof(IReadOnlyList<BluetoothCandidate>))]
[JsonSerializable(typeof(List<BluetoothCandidate>))]
[JsonSerializable(typeof(UiPreviewAudioOutput[]))]
internal partial class HostJsonContext : JsonSerializerContext;

internal static class HostWire
{
    internal static readonly JsonSerializerOptions Json = new(RpcWire.Json)
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(RpcJsonContext.Default, HostJsonContext.Default, new DefaultJsonTypeInfoResolver())
    };
}
