using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeyMelody.Performance;

/// <summary>Explicit opt-in benchmark arguments shared by the two published executables.</summary>
public sealed record BenchmarkOptions(string RunId, string Output, string Scenario, int Seconds, int WarmupSeconds,
    string? HostExecutable, bool HostReady, bool Diagnostics = false)
{
    public string PipeName => "HeyMelody-benchmark-" + HeyMelody.Platform.DesktopRuntime.UserSessionKey + "-" + RunId;
    public string HostMutex => "Local\\HeyMelody.Benchmark.Host-" + HeyMelody.Platform.DesktopRuntime.UserSessionKey + "-" + RunId;
    public string MainMutex => "Local\\HeyMelody.Benchmark.Main-" + HeyMelody.Platform.DesktopRuntime.UserSessionKey + "-" + RunId;
    public string StopFile => Path.Combine(Output, "stop");

    public static bool TryParse(string[] args, out BenchmarkOptions? options)
    {
        options = null;
        if (args.FirstOrDefault() != "--performance-benchmark" || args.Length % 2 != 1) return false;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] allowed = ["--run-id", "--output-dir", "--scenario", "--seconds", "--warmup-seconds", "--host-executable", "--host-ready", "--diagnostics"];
        for (int i = 1; i < args.Length; i += 2)
            if (!allowed.Contains(args[i], StringComparer.Ordinal) || !values.TryAdd(args[i], args[i + 1])) return false;
        if (!Guid.TryParseExact(values.GetValueOrDefault("--run-id"), "N", out var run)
            || !values.TryGetValue("--output-dir", out var output) || !Path.IsPathFullyQualified(output)) return false;
        var scenario = values.GetValueOrDefault("--scenario", "ui-static");
        if (scenario is not ("ui-static" or "static-idle" or "static-scroll" or "model" or "model-scroll" or "integrated-static" or "host")) return false;
        if (!int.TryParse(values.GetValueOrDefault("--seconds", "30"), out var seconds) || seconds is < 0 or > 120
            || !int.TryParse(values.GetValueOrDefault("--warmup-seconds", "5"), out var warmup) || warmup is < 0 or > 60
            || !bool.TryParse(values.GetValueOrDefault("--host-ready", "false"), out var ready)
            || !bool.TryParse(values.GetValueOrDefault("--diagnostics", "false"), out var diagnostics)) return false;
        var executable = values.GetValueOrDefault("--host-executable");
        if (scenario == "integrated-static" && (executable is null || !Path.IsPathFullyQualified(executable) || !File.Exists(executable))) return false;
        options = new(run.ToString("N"), Path.GetFullPath(output), scenario, seconds, warmup, executable, ready, diagnostics);
        return true;
    }

    public void PrepareOutput()
    {
        if (File.Exists(Output) || Directory.Exists(Output) && Directory.EnumerateFileSystemEntries(Output).Any())
            throw new IOException("Benchmark output must be a new or empty directory.");
        Directory.CreateDirectory(Output);
        using var marker = new FileStream(Path.Combine(Output, "benchmark-created"), FileMode.CreateNew, FileAccess.Write);
    }
}

public sealed record BenchmarkProcess(int ProcessId, string Role, string Executable, string Sha256, string Architecture,
    string Runtime, bool DynamicCodeSupported, bool DynamicCodeCompiled, long StartedUtcTicks)
{
    public static BenchmarkProcess Capture(string role, bool includeHash = true)
    {
        var path = Environment.ProcessPath ?? throw new IOException("Executable path unavailable.");
        string hash = "";
        if (includeHash)
        {
            using var input = File.OpenRead(path);
            hash = Convert.ToHexStringLower(SHA256.HashData(input));
        }
        using var process = Process.GetCurrentProcess();
        return new(Environment.ProcessId, role, path, hash,
            RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.FrameworkDescription,
            RuntimeFeature.IsDynamicCodeSupported, RuntimeFeature.IsDynamicCodeCompiled,
            process.StartTime.ToUniversalTime().Ticks);
    }
}

public sealed record BenchmarkSample(double Seconds, double CpuMilliseconds, long WorkingSetBytes, long PrivateBytes,
    long AllocatedBytes, int[] GcCollections, string? EnergyMode = null)
{
    public static BenchmarkSample Capture(Stopwatch timer, string? energyMode = null)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new(timer.Elapsed.TotalSeconds, process.TotalProcessorTime.TotalMilliseconds, process.WorkingSet64,
            process.PrivateMemorySize64, GC.GetTotalAllocatedBytes(false),
            [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)], energyMode);
    }
}

public sealed class BenchmarkReport
{
    public int SchemaVersion { get; init; } = 1;
    public string HarnessRevision { get; init; } = "published-aot-comparison-v1";
    public required string RunId { get; init; }
    public required string Scenario { get; init; }
    public required string Role { get; init; }
    public BenchmarkProcess? Process { get; set; }
    public bool Success { get; set; }
    public string? Failure { get; set; }
    public double ReadyMilliseconds { get; set; }
    public int WarmupSeconds { get; set; }
    public double MeasurementSeconds { get; set; }
    public int NativeBluetoothCalls { get; init; }
    public int SimulatedWorkerCount { get; init; }
    public bool RealTray { get; init; }
    public bool DiagnosticsEnabled { get; init; }
    public string Scope { get; init; } = "Published executable; fixed local fixture; no headset, online resources or device writes.";
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public double RenderScaling { get; set; }
    public string? ModelSha256 { get; set; }
    public string? ActualModelBackend { get; set; }
    public List<BenchmarkSample> Samples { get; } = [];
    public List<double> UiCallbackIntervalsMilliseconds { get; } = [];
    public List<double> BitmapCopyIntervalsMilliseconds { get; } = [];

    public void Write(string directory, string name = "benchmark.json")
    {
        var path = Path.Combine(directory, name);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, this, PerformanceBenchmarkJson.Default.BenchmarkReport);
        File.Move(temporary, path, overwrite: false);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BenchmarkReport))]
[JsonSerializable(typeof(BenchmarkProcess))]
public partial class PerformanceBenchmarkJson : JsonSerializerContext;
