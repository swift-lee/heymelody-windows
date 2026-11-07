using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HeyMelody.Rendering.Filament;
using Microsoft.Win32.SafeHandles;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This audit is scoped to Windows.");
var options = Parse(args);
var output = Path.GetFullPath(Required(options, "--output-dir"));
if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Output must be a NEW directory.");
var modelPath = Path.GetFullPath(Required(options, "--model"));
if (new FileInfo(modelPath).Length > FilamentModelData.MaximumBytes) throw new InvalidDataException("GLB is too large.");
var backend = Required(options, "--backend") switch
{
    "vulkan" => FilamentBackend.Vulkan,
    "opengl" => FilamentBackend.OpenGL,
    _ => throw new ArgumentException("Backend must be vulkan or opengl.")
};
var size = Int(options, "--size", 640, 32, 2048);
var msaa = Int(options, "--msaa", 4, 1, 4);
if (msaa is not (1 or 4)) throw new ArgumentException("MSAA must be 1 or 4.");
var warmupSeconds = Int(options, "--warmup", 5, 1, 30);
var seconds = Int(options, "--seconds", 15, 5, 120);
var requestHz = Int(options, "--request-hz", 60, 0, 240);
using var exclusive = new Mutex(false, @"Local\HeyMelody.NativePerformanceAudit");
if (!exclusive.WaitOne(0)) throw new InvalidOperationException("Another native benchmark is already running.");
Directory.CreateDirectory(output);
var json = new JsonSerializerOptions { WriteIndented = true };
var runtimeRid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
var runtimePath = Path.Combine(AppContext.BaseDirectory, "runtimes", runtimeRid, "native");
var manifestPath = Path.Combine(runtimePath, "filament-runtime.json");
if (!File.Exists(manifestPath)) throw new FileNotFoundException("No explicitly packaged benchmark runtime.", manifestPath);
using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
var manifestRoot = manifest.RootElement;
if (manifestRoot.GetProperty("abiVersion").GetInt32() != 2
    || manifestRoot.GetProperty("schemaVersion").GetInt32() != 2
    || manifestRoot.GetProperty("logCallbackExport").GetString() != "hm_set_log_callback")
    throw new InvalidDataException("The current native runtime contract is required; legacy runtimes are not accepted.");
var model = FilamentModelData.CreateValidated(File.ReadAllBytes(modelPath));
var nativePath = Path.Combine(runtimePath, "HeyMelody.Filament.Native.dll");
var hashes = new
{
    model = new { file = Path.GetFileName(modelPath), model.Sha256, bytes = model.Bytes.Length },
    native = new { sha256 = Hash(nativePath), manifestSha256 = Hash(manifestPath),
        probeSha256 = Hash(Path.Combine(runtimePath, "hm_filament_probe.exe")), path = nativePath },
    managedRenderer = new { path = typeof(IFilamentRenderer).Assembly.Location,
        sha256 = Hash(typeof(IFilamentRenderer).Assembly.Location) }
};
File.WriteAllText(Path.Combine(output, "input.json"), JsonSerializer.Serialize(new
{
    schemaVersion = 1, scope = "Current managed/native renderer, no Avalonia, no headset service, no texture upload or DWM presentation.",
    backend = backend.ToString(), size, msaa, warmupSeconds, seconds, requestHz, runtimeRid, hashes,
    nativeManifest = manifestRoot.Clone(),
    process = new { architecture = RuntimeInformation.ProcessArchitecture.ToString(), osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
        Environment.ProcessorCount, framework = RuntimeInformation.FrameworkDescription },
    camera = new { yawSpeedDegreesPerSecond = 20, pitchDegrees = 15, verticalFovDegrees = 45,
        zoom = 1, fitMargin = 1.08, exposure = 1, lightIntensity = 100000, clear = "243/255" },
    waitStrategy = HighResolutionWaiter.Description,
    requestScope = requestHz == 0 ? "Serialized: one outstanding RequestFrame until a newer frame is copied."
        : "Paced asynchronous requests; native requests can coalesce. Actual rates must be checked, not inferred from the target."
}, json));

string? failure = null;
var calls = new List<CallSample>(seconds * 2000);
var frames = new List<FrameSample>(seconds * Math.Max(60, requestHz));
var buffer = new byte[checked(size * size * 4)];
var configuration = new FilamentRenderConfiguration(size, size, PitchRadians: MathF.PI / 12,
    ClearRed: 243f / 255, ClearGreen: 243f / 255, ClearBlue: 243f / 255, MsaaSamples: msaa);
ulong afterFrameId = 0;
double phaseBase = 0;
PhaseResult? measured = null;
FilamentDiagnostics? before = null, atEnd = null, afterDrain = null;
double? waitInitializationMilliseconds = null;
try
{
    using var waiter = new HighResolutionWaiter();
    waitInitializationMilliseconds = waiter.WaitOneMillisecond();
    using var renderer = NativeFilamentRendererFactory.Instance.Create(backend);
    renderer.LoadModel(model);
    renderer.Configure(configuration);
    renderer.RequestFrame();
    var firstFrameDeadline = Stopwatch.StartNew();
    while (!renderer.TryCopyLatestFrame(afterFrameId, buffer, out var firstFrame))
    {
        if (firstFrameDeadline.Elapsed.TotalSeconds > 30) throw new TimeoutException("No first frame.");
        waiter.WaitOneMillisecond();
    }
    // The loading request is outside both warmup and measurement.
    if (renderer.TryCopyLatestFrame(0, buffer, out var initial)) afterFrameId = initial.FrameId;
    RunPhase(renderer, waiter, warmupSeconds, false);
    Drain(renderer, waiter, .5, false);
    before = renderer.GetDiagnostics();
    measured = RunPhase(renderer, waiter, seconds, true);
    atEnd = renderer.GetDiagnostics();
    Drain(renderer, waiter, .5, true);
    afterDrain = renderer.GetDiagnostics();
    if (atEnd.ActualBackend != backend || atEnd.Width != size || atEnd.Height != size || atEnd.MsaaSamples != msaa)
        throw new InvalidOperationException("Observed backend or dimensions differ from the requested experiment.");
}
catch (Exception error)
{
    failure = error.ToString();
}
finally
{
    using (var raw = new StreamWriter(new FileStream(Path.Combine(output, "calls.jsonl"), FileMode.CreateNew)))
        foreach (var row in calls) raw.WriteLine(JsonSerializer.Serialize(row));
    using (var raw = new StreamWriter(new FileStream(Path.Combine(output, "frames.jsonl"), FileMode.CreateNew)))
        foreach (var row in frames) raw.WriteLine(JsonSerializer.Serialize(row));
    var measuredFrames = frames.Where(frame => frame.Phase == "measurement").ToArray();
    var measuredCalls = calls.Where(call => call.Phase == "measurement").ToArray();
    var requestCount = measuredCalls.Count(call => call.Kind == "RequestFrame");
    var pollCount = measuredCalls.Count(call => call.Kind == "TryCopy");
    var skippedIds = measuredFrames.Zip(measuredFrames.Skip(1), (a, b) => b.FrameId > a.FrameId ? b.FrameId - a.FrameId - 1 : 0).Sum(value => (double)value);
    var frameIntervals = measuredFrames.Zip(measuredFrames.Skip(1), (a, b) => (b.Seconds - a.Seconds) * 1000).ToArray();
    var report = new
    {
        schemaVersion = 2, success = failure is null && measured is not null, failure,
        backend = backend.ToString(), size, msaa, warmupSeconds, requestedSeconds = seconds, requestHz,
        elapsedSeconds = measured?.Seconds, processCpuSeconds = measured?.CpuSeconds,
        processCpuPercentOneCore = measured is { Seconds: > 0 } p ? 100 * p.CpuSeconds / p.Seconds : (double?)null,
        processCpuPercentMachine = measured is { Seconds: > 0 } q ? 100 * q.CpuSeconds / q.Seconds / Environment.ProcessorCount : (double?)null,
        allocatedBytes = measured?.AllocatedBytes, gcCollections = measured?.GcCollections,
        requestCalls = requestCount, tryCopyCalls = pollCount,
        actualRequestHz = measured is { Seconds: > 0 } r ? requestCount / r.Seconds : (double?)null,
        pollCallHz = measured is { Seconds: > 0 } t ? pollCount / t.Seconds : (double?)null,
        waitStrategy = HighResolutionWaiter.Description, waitInitializationMilliseconds,
        waitWallMilliseconds = Stats(measuredCalls.Where(call => call.Kind == "HighResolutionWait").Select(call => call.Milliseconds)),
        copiedFrames = measuredFrames.Length,
        copiedFramesPerSecond = measured is { Seconds: > 0 } m ? measuredFrames.Length / m.Seconds : (double?)null,
        observedCompletedFrameIdAdvance = before is not null && atEnd is not null ? atEnd.FrameId - before.FrameId : (ulong?)null,
        skippedFrameIdsBetweenCopies = skippedIds,
        actualSubmissionCount = (long?)null, exactCompletionCount = (long?)null,
        countScope = "RequestFrame calls are not native submissions: requests coalesce. FrameIdAdvance is the advance in maximum completed sequence, not an exact completion count in an out-of-order asynchronous pipeline.",
        configureWallMilliseconds = Stats(measuredCalls.Where(call => call.Kind == "Configure").Select(call => call.Milliseconds)),
        requestWallMilliseconds = Stats(measuredCalls.Where(call => call.Kind == "RequestFrame").Select(call => call.Milliseconds)),
        tryCopyWallMilliseconds = Stats(measuredCalls.Where(call => call.Kind == "TryCopy").Select(call => call.Milliseconds)),
        successfulCopyWallMilliseconds = Stats(measuredCalls.Where(call => call.Kind == "TryCopy" && call.HadFrame).Select(call => call.Milliseconds)),
        nativeRenderCpuWallMilliseconds = Stats(measuredFrames.Select(frame => frame.NativeRenderMilliseconds)),
        nativeReadbackWallMilliseconds = Stats(measuredFrames.Select(frame => frame.NativeReadbackMilliseconds)),
        copiedFrameIntervalMilliseconds = Stats(frameIntervals),
        before, atEnd, afterDrain,
        timingScope = "All per-call records and all COPIED new-frame statistics are retained, not a rolling 120-frame window. Skipped frames are not observed. Native render is CPU submit wall time; readback includes GPU/driver queuing, callback pumping and CPU background compositing, NOT GPU timestamp timing.",
        exclusions = "No Avalonia/WriteableBitmap, no CPU-to-Skia texture upload, no DWM. Managed sampling and per-process high-resolution waitable-timer polling overhead ARE included. No global timer, power or system settings are changed. Drain after measurement is excluded from measured counts and CPU.",
        input = hashes
    };
    File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(report, json));
    Console.WriteLine(JsonSerializer.Serialize(new { report.success, backend = backend.ToString(), size, msaa,
        report.copiedFramesPerSecond, output, failure }));
    exclusive.ReleaseMutex();
}
return failure is null ? 0 : 1;

PhaseResult RunPhase(IFilamentRenderer renderer, HighResolutionWaiter waiter, int duration, bool capture)
{
    var phase = capture ? "measurement" : "warmup";
    var clock = Stopwatch.StartNew();
    using var process = Process.GetCurrentProcess();
    var cpuStart = process.TotalProcessorTime.TotalSeconds;
    var allocatedStart = GC.GetTotalAllocatedBytes(false);
    var collectionsStart = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
    double nextRequest = 0;
    bool awaitingFrame = false;
    while (clock.Elapsed.TotalSeconds < duration)
    {
        var now = clock.Elapsed.TotalSeconds;
        if (requestHz == 0 ? !awaitingFrame : now >= nextRequest)
        {
            if (requestHz > 0)
            {
                nextRequest += 1d / requestHz;
                if (nextRequest <= now) nextRequest = now + 1d / requestHz;
            }
            var yaw = (float)((phaseBase + now) * Math.PI / 9);
            var start = Stopwatch.GetTimestamp();
            renderer.Configure(configuration with { YawRadians = yaw });
            if (capture) calls.Add(new(phase, "Configure", now, Stopwatch.GetElapsedTime(start).TotalMilliseconds, false, 0));
            start = Stopwatch.GetTimestamp();
            renderer.RequestFrame();
            if (capture) calls.Add(new(phase, "RequestFrame", now, Stopwatch.GetElapsedTime(start).TotalMilliseconds, false, 0));
            awaitingFrame = true;
        }
        var copyStart = Stopwatch.GetTimestamp();
        var copied = renderer.TryCopyLatestFrame(afterFrameId, buffer, out var frame);
        var copyMilliseconds = Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds;
        var copiedAt = clock.Elapsed.TotalSeconds;
        if (capture) calls.Add(new(phase, "TryCopy", copiedAt, copyMilliseconds, copied, copied ? frame.FrameId : 0));
        if (copied)
        {
            Validate(frame);
            afterFrameId = frame.FrameId;
            awaitingFrame = false;
            if (capture) frames.Add(new(phase, copiedAt, frame.FrameId, frame.Generation, frame.Width, frame.Height,
                frame.RenderMilliseconds, frame.ReadbackMilliseconds, copyMilliseconds));
        }
        var waitMilliseconds = waiter.WaitOneMillisecond();
        if (capture) calls.Add(new(phase, "HighResolutionWait", clock.Elapsed.TotalSeconds, waitMilliseconds, false, 0));
    }
    var elapsed = clock.Elapsed.TotalSeconds;
    var cpu = process.TotalProcessorTime.TotalSeconds - cpuStart;
    var allocated = GC.GetTotalAllocatedBytes(false) - allocatedStart;
    var collections = Enumerable.Range(0, 3).Select(generation => GC.CollectionCount(generation) - collectionsStart[generation]).ToArray();
    phaseBase += elapsed;
    return new(elapsed, cpu, allocated, collections);
}

void Drain(IFilamentRenderer renderer, HighResolutionWaiter waiter, double duration, bool capture)
{
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed.TotalSeconds < duration)
    {
        var start = Stopwatch.GetTimestamp();
        var copied = renderer.TryCopyLatestFrame(afterFrameId, buffer, out var frame);
        var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (capture) calls.Add(new("drain", "TryCopy", clock.Elapsed.TotalSeconds, milliseconds, copied, copied ? frame.FrameId : 0));
        if (copied)
        {
            Validate(frame);
            afterFrameId = frame.FrameId;
            if (capture) frames.Add(new("drain", clock.Elapsed.TotalSeconds, frame.FrameId, frame.Generation,
                frame.Width, frame.Height, frame.RenderMilliseconds, frame.ReadbackMilliseconds, milliseconds));
        }
        var waitMilliseconds = waiter.WaitOneMillisecond();
        if (capture) calls.Add(new("drain", "HighResolutionWait", clock.Elapsed.TotalSeconds, waitMilliseconds, false, 0));
    }
}

void Validate(FilamentFrameInfo frame)
{
    if (frame.Width != size || frame.Height != size || frame.RowBytes != size * 4 || frame.FrameId <= afterFrameId)
        throw new InvalidDataException("Unexpected or stale frame layout.");
}

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
static string Required(Dictionary<string, string> options, string key)
    => options.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing {key}.");
static int Int(Dictionary<string, string> options, string key, int fallback, int minimum, int maximum)
    => !options.TryGetValue(key, out var value) ? fallback
        : int.TryParse(value, out var number) && number >= minimum && number <= maximum ? number
        : throw new ArgumentException($"Invalid {key}.");
static Dictionary<string, string> Parse(string[] args)
{
    var allowed = new[] { "--backend", "--size", "--msaa", "--model", "--output-dir", "--warmup", "--seconds", "--request-hz" };
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i < args.Length; i += 2)
        if (i + 1 >= args.Length || !allowed.Contains(args[i]) || !values.TryAdd(args[i], args[i + 1]))
            throw new ArgumentException("Expected unique --backend vulkan|opengl --size N --msaa 1|4 --model path --output-dir NEW [--request-hz 60|0] [--warmup 5 --seconds 15].");
    return values;
}
static object Stats(IEnumerable<double> samples)
{
    var sorted = samples.Order().ToArray();
    double? P(double percentile) => sorted.Length == 0 ? null
        : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
    return new { count = sorted.Length, mean = sorted.Length == 0 ? (double?)null : sorted.Average(),
        minimum = sorted.Length == 0 ? (double?)null : sorted[0], median = P(.5), p95 = P(.95), p99 = P(.99),
        maximum = sorted.Length == 0 ? (double?)null : sorted[^1] };
}

internal readonly record struct CallSample(string Phase, string Kind, double Seconds, double Milliseconds, bool HadFrame, ulong FrameId);
internal readonly record struct FrameSample(string Phase, double Seconds, ulong FrameId, ulong Generation,
    int Width, int Height, double NativeRenderMilliseconds, double NativeReadbackMilliseconds, double CopyMilliseconds);
internal sealed record PhaseResult(double Seconds, double CpuSeconds, long AllocatedBytes, int[] GcCollections);

internal sealed class HighResolutionWaiter : IDisposable
{
    internal const string Description = "Windows per-process waitable timer: CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, relative -10000 (1 ms), one-shot; no timeBeginPeriod or fallback.";
    private const uint HighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private readonly SafeWaitHandle _timer;

    internal HighResolutionWaiter()
    {
        _timer = CreateWaitableTimerExW(IntPtr.Zero, null, HighResolution, TimerAllAccess);
        if (_timer.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            _timer.Dispose();
            throw new Win32Exception(error, "High-resolution waitable timer creation failed; the benchmark will not fall back to a coarse timer.");
        }
    }

    internal double WaitOneMillisecond()
    {
        var started = Stopwatch.GetTimestamp();
        long dueTime = -10_000;
        if (!SetWaitableTimer(_timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not arm the high-resolution waitable timer.");
        uint result = WaitForSingleObject(_timer, 5000);
        if (result == 0xFFFFFFFF)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "High-resolution waitable timer wait failed.");
        if (result != 0)
            throw new InvalidOperationException($"High-resolution waitable timer did not signal successfully (wait result 0x{result:X8}); no coarse fallback is allowed.");
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    public void Dispose() => _timer.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period,
        IntPtr completionRoutine, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
