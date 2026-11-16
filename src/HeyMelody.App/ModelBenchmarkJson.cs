using System.Text.Json.Serialization;
using HeyMelody.Performance;
using HeyMelody.Rendering.Filament;

namespace HeyMelodyWindows.App;

internal sealed record ModelBenchmarkProcess(int ProcessId, string ProcessName, DateTimeOffset StartedAtUtc, string Model,
    string Backend, string? ModelFile, int ColdRuns, int HotRuns, string Scope);
internal sealed record ModelBenchmarkFailure(bool Success, int ProcessId, string Failure, string Scope);
internal sealed record ModelBenchmarkRun(int Sample, string Kind, DateTimeOffset MeasuredAtUtc, ModelLoadTimings Timings);
internal sealed record ModelBenchmarkViewport(double Width, double Height, double RenderScaling);
internal sealed record ModelBenchmarkScreenshot(string File, string PixelFormat, int Width, int Height, ulong FrameId,
    double Yaw, double Pitch, ulong SourceTriangles, int SourceMeshes);
internal sealed record ModelBenchmarkReport(bool Success, int ProcessId, int NativeBluetoothCalls, string ProcessName,
    string Scope, string Model, string Backend, int HotRuns, string AppAssemblySha256, string? NativeLibrarySha256,
    string? ModelSha256, ModelBenchmarkViewport? Viewport, List<ModelBenchmarkRun> Runs,
    ModelBenchmarkScreenshot? Screenshot, string? Failure, BenchmarkProcess Process);
internal sealed record ModelTraceSession(string Kind, int SchemaVersion, int ProcessId, string? Executable,
    string Os, string Architecture, DateTimeOffset StartedUtc);
internal sealed record ModelTraceLoad(string Kind, int ProcessId, DateTimeOffset MeasuredUtc, string? ModelSha256,
    ModelLoadTimings Timings);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ModelBenchmarkProcess))]
[JsonSerializable(typeof(ModelBenchmarkFailure))]
[JsonSerializable(typeof(ModelBenchmarkRun))]
[JsonSerializable(typeof(ModelBenchmarkReport))]
[JsonSerializable(typeof(ModelTraceSession))]
[JsonSerializable(typeof(ModelTraceLoad))]
[JsonSerializable(typeof(ModelLoadTimings))]
internal partial class ModelBenchmarkJson : JsonSerializerContext;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ModelLoadTimings))]
[JsonSerializable(typeof(FilamentDiagnostics))]
internal partial class ModelDiagnosticJson : JsonSerializerContext;
