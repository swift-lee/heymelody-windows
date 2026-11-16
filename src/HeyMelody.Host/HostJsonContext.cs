using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
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
[JsonSerializable(typeof(WorkerDiagnostics))]
internal partial class HostJsonContext : JsonSerializerContext;

internal static class HostWire
{
    internal static readonly JsonSerializerOptions Json = new(RpcWire.Json)
    {
        TypeInfoResolver = CreateResolver()
    };

    internal static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Json.GetTypeInfo(typeof(T));

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Native AOT sets IsReflectionEnabledByDefault=false and removes this reflection-only JIT compatibility branch.")]
    [UnconditionalSuppressMessage("Aot", "IL3050",
        Justification = "Native AOT sets IsReflectionEnabledByDefault=false and removes this reflection-only JIT compatibility branch.")]
    private static IJsonTypeInfoResolver CreateResolver() => JsonSerializer.IsReflectionEnabledByDefault
        ? JsonTypeInfoResolver.Combine(RpcJsonContext.Default, HostJsonContext.Default, new DefaultJsonTypeInfoResolver())
        : JsonTypeInfoResolver.Combine(RpcJsonContext.Default, HostJsonContext.Default);
}
