using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using HeyMelody.Rendering.Filament;
using HeyMelodyWindows.Ui;

namespace HeyMelodyWindows.App;

/// <summary>Measures the real Win32/Avalonia model lifecycle without connecting to a headset.</summary>
internal static class ModelLoadingBenchmark
{
    private static State _state = null!;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static int Run(string[] arguments)
    {
        if (!TryParse(arguments, out var options))
        {
            Console.Error.WriteLine("Usage: --model-loading-benchmark --output-dir path [--backend vulkan|opengl] [--model free4|dynaudio|file] [--file path] [--hot-runs 5..30]");
            return 2;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Model loading performance requires the real Windows native renderer.");
            return 2;
        }
        if (Directory.Exists(options.Output) && Directory.EnumerateFileSystemEntries(options.Output).Any())
        {
            Console.Error.WriteLine("Use a new or empty output directory so previous benchmark evidence is preserved.");
            return 2;
        }
        Directory.CreateDirectory(options.Output);
        _state = new(options);
        File.WriteAllText(Path.Combine(options.Output, "process.json"), JsonSerializer.Serialize(new
        {
            processId = Environment.ProcessId, processName = Process.GetCurrentProcess().ProcessName,
            startedAtUtc = DateTimeOffset.UtcNow, options.Model, backend = options.Backend.ToString(),
            modelFile = options.File, coldRuns = 1, options.HotRuns,
            scope = "isolated production model-control scene in the named process; no device-selection or artwork RPC path; full product-window private-working-set acceptance is separate"
        }, Json));
        var thread = new Thread(() =>
        {
            var oldPath = ThemePreferences.SettingsPathOverride;
            var oldCompatibility = ModelRenderingPreferences.CompatibilityMode;
            var oldFrameRate = ModelRenderingPreferences.FrameRate;
            ThemePreferences.SettingsPathOverride = Path.Combine(options.Output, "unwritten-preferences.json");
            try
            {
                ModelRenderingPreferences.Reload();
                ModelRenderingPreferences.Apply(options.Backend == FilamentBackend.OpenGL);
                DeviceModelView.DefaultFactoryOverride = null;
                AppBuilder.Configure<BenchmarkApplication>().UsePlatformDetect()
                    .UseHeyMelodyRendering(UiRenderingPreferenceReader.Load()).UseHeyMelodyTypography().LogToTrace()
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception error) { _state.Failure = error.ToString(); }
            finally
            {
                ThemePreferences.SettingsPathOverride = oldPath;
                ModelRenderingPreferences.Apply(oldCompatibility, oldFrameRate);
            }
        }) { IsBackground = true, Name = "HeyMelody native model loading benchmark" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(8)))
        {
            // The UI may still own native work and mutate its receipts. Do not enumerate that state.
            Volatile.Read(ref _state.RequestStop)?.Invoke();
            if (!thread.Join(TimeSpan.FromSeconds(5)))
            {
                var timeout = new { success = false, processId = Environment.ProcessId,
                    failure = "The native benchmark exceeded eight minutes and did not stop within the five-second shutdown budget.",
                    scope = "timeout receipt only; the active UI/native state was not inspected" };
                File.WriteAllText(Path.Combine(options.Output, "model-loading-timeout.json"), JsonSerializer.Serialize(timeout, Json));
                Console.WriteLine(JsonSerializer.Serialize(timeout));
                return 1;
            }
            _state.Failure ??= "The native benchmark exceeded eight minutes.";
        }
        var report = new
        {
            success = _state.Completed && _state.Failure is null,
            processId = Environment.ProcessId, nativeBluetoothCalls = 0,
            processName = Process.GetCurrentProcess().ProcessName,
            scope = "isolated production model-control scene; real Filament/Win32, one cold attachment and repeated detach/non-model/reattach; no backend switching, device-selection or artwork RPC; full product-window private-working-set acceptance is separate",
            options.Model, backend = options.Backend.ToString(), options.HotRuns,
            appAssemblySha256 = HashFile(typeof(DeviceModelView).Assembly.Location),
            nativeLibrarySha256 = _state.NativeLibrarySha256,
            modelSha256 = _state.ModelSha256, viewport = _state.Viewport,
            runs = _state.Runs, screenshot = _state.Screenshot, failure = _state.Failure
        };
        File.WriteAllText(Path.Combine(options.Output, "model-loading.json"), JsonSerializer.Serialize(report, Json));
        Console.WriteLine(JsonSerializer.Serialize(new { report.success, options.Model, backend = options.Backend.ToString(), runs = _state.Runs.Count, output = options.Output, report.failure }));
        return report.success ? 0 : 1;
    }

    private static bool TryParse(string[] arguments, out Options options)
    {
        var backend = FilamentBackend.Vulkan;
        string model = "dynaudio";
        string? output = null, file = null;
        int hotRuns = 5;
        for (int index = 1; index < arguments.Length; index++)
        {
            string key = arguments[index];
            if (++index >= arguments.Length) { options = null!; return false; }
            string value = arguments[index];
            if (key == "--backend" && value is "vulkan" or "opengl") backend = value == "opengl" ? FilamentBackend.OpenGL : FilamentBackend.Vulkan;
            else if (key == "--model" && value is "free4" or "dynaudio" or "file") model = value;
            else if (key == "--output-dir") output = Path.GetFullPath(value);
            else if (key == "--file") file = Path.GetFullPath(value);
            else if (key == "--hot-runs" && int.TryParse(value, out var count) && count is >= 5 and <= 30) hotRuns = count;
            else { options = null!; return false; }
        }
        options = new(output ?? "", backend, model, file, hotRuns);
        return output is not null && (model == "file" ? file is not null && File.Exists(file) : file is null);
    }

    public sealed class BenchmarkApplication : App
    {
        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
            var host = new ContentControl();
            var window = new Window
            {
                Title = "HeyMelody model loading benchmark", Width = 480, Height = 640,
                CanResize = false, Content = host, ShowActivated = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            desktop.MainWindow = window;
            Volatile.Write(ref _state.RequestStop, () => Dispatcher.UIThread.Post(() =>
            {
                window.Close();
                desktop.Shutdown(1);
            }));
            window.Opened += (_, _) => _ = MeasureAsync(window, host, desktop);
        }
    }

    private static DeviceModelView CreateModel()
    {
        if (_state.Options.Model == "free4")
        {
            _state.ModelSha256 = EncoFree4ModelView.ModelSha256;
            return new EncoFree4ModelView();
        }
        if (_state.Options.Model == "dynaudio")
        {
            _state.ModelSha256 = EncoFree4DynaudioModelView.ModelSha256;
            return new EncoFree4DynaudioModelView();
        }
        string file = _state.Options.File!;
        _state.ModelSha256 = HashFile(file);
        return new DeviceModelView(new DeviceModelVisual(new ArtworkFile(new Uri(file), Path.GetDirectoryName(file)!, _state.ModelSha256), DeviceVisuals.GenericProduct));
    }

    private static async Task MeasureAsync(Window window, ContentControl host, IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var model = CreateModel();
            for (int index = 0; index <= _state.Options.HotRuns; index++)
            {
                var completion = new TaskCompletionSource<ModelLoadTimings>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnMeasured(DeviceModelView measured, ModelLoadTimings timings)
                {
                    if (ReferenceEquals(measured, model)) completion.TrySetResult(timings);
                }
                DeviceModelView.LoadMeasured += OnMeasured;
                ModelLoadTimings result;
                try
                {
                    host.Content = model;
                    var deadline = Stopwatch.StartNew();
                    while (!completion.Task.IsCompleted)
                    {
                        if (model.LoadError is { } error) throw new InvalidOperationException(error);
                        if (deadline.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("No matching first model frame within ninety seconds.");
                        await Task.Delay(25);
                    }
                    result = await completion.Task;
                }
                finally { DeviceModelView.LoadMeasured -= OnMeasured; }
                if (model.Diagnostics.ActualBackend != _state.Options.Backend || !model.ModelReady || model.LoadError is not null)
                    throw new InvalidOperationException("The renderer did not present using the requested native backend.");
                // Capture identity while the first engine is alive; driver module teardown can race enumeration.
                _state.NativeLibrarySha256 ??= await CaptureNativeLibraryHashAsync();
                var receipt = new { sample = index, kind = index == 0 ? "cold" : "hot", measuredAtUtc = DateTimeOffset.UtcNow, timings = result };
                _state.Runs.Add(receipt);
                File.AppendAllText(Path.Combine(_state.Options.Output, "measurements.jsonl"), JsonSerializer.Serialize(receipt) + Environment.NewLine);
                if (index < _state.Options.HotRuns)
                {
                    // Keep the selected backend and in-process model cache, but actually retire the view.
                    host.Content = DeviceVisuals.DefaultTemplate.Build(DeviceVisuals.GenericProduct);
                    await Task.Delay(100);
                }
            }
            _state.Viewport = new { model.Bounds.Width, model.Bounds.Height, renderScaling = window.RenderScaling };
            // Capture after measurement at a reproducible pose, without changing timed rendering behavior.
            long cameraBefore = model.CameraUpdateCount;
            ulong frameBefore = model.Diagnostics.FrameId;
            bool poseChanged = Math.Abs(model.Motion.Yaw) > 1e-9
                || Math.Abs(model.Motion.Pitch - ModelOrbitMotion.InitialPitch) > 1e-9;
            model.Motion.RestoreMotion(new(0, ModelOrbitMotion.InitialPitch, 0, 0, 0, ModelOrbitPhase.Idle));
            model.Motion.BeginDrag(0, 0, 0);
            var captureDeadline = Stopwatch.StartNew();
            while (poseChanged && (model.CameraUpdateCount <= cameraBefore || model.Diagnostics.FrameId <= frameBefore))
            {
                if (captureDeadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The fixed-pose native frame did not arrive.");
                await Task.Delay(25);
            }
            // Drain any older asynchronous readback before saving the fixed-pose model frame.
            await Task.Delay(300);
            SaveFrame(model);
            if (RenderDiagnostics.Enabled)
            {
                // Capture only the changed model/diagnostic region, without a desktop screenshot.
                using var overlay = new RenderTargetBitmap(new PixelSize(
                    (int)Math.Ceiling(model.Bounds.Width), (int)Math.Ceiling(model.Bounds.Height)), new Vector(96, 96));
                overlay.Render(model);
                using var output = new FileStream(Path.Combine(_state.Options.Output, "diagnostics.png"), FileMode.CreateNew, FileAccess.Write);
                overlay.Save(output, PngBitmapEncoderOptions.Default);
            }
            _state.Completed = true;
        }
        catch (Exception error) { _state.Failure = error.ToString(); }
        finally
        {
            host.Content = null;
            window.Close();
            desktop.Shutdown(_state.Failure is null ? 0 : 1);
        }
    }

    private static void SaveFrame(DeviceModelView model)
    {
        var pixels = model.CaptureLatestFrame(out var frame) ?? throw new InvalidOperationException("No native frame to capture.");
        string path = Path.Combine(_state.Options.Output, "model.png");
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(PixelFormat.Rgba8888, AlphaFormat.Opaque, pin.AddrOfPinnedObject(),
                new PixelSize(frame.Width, frame.Height), new Vector(96, 96), frame.RowBytes);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            bitmap.Save(output, PngBitmapEncoderOptions.Default);
        }
        finally { pin.Free(); }
        _state.Screenshot = new { file = "model.png", pixelFormat = "RGBA8", frame.Width, frame.Height, frame.FrameId,
            yaw = model.Motion.Yaw, pitch = model.Motion.Pitch, model.Diagnostics.SourceTriangles, model.Diagnostics.SourceMeshes };
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static async Task<string> CaptureNativeLibraryHashAsync()
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                var module = process.Modules.Cast<ProcessModule>().FirstOrDefault(candidate =>
                    candidate.ModuleName.Contains("HeyMelody.Filament.Native", StringComparison.OrdinalIgnoreCase));
                if (module is not null) return HashFile(module.FileName);
                lastError = new InvalidOperationException("The active model renderer has no identifiable native module.");
            }
            catch (Win32Exception error) { lastError = error; }
            if (attempt < 2) await Task.Delay(25 * (attempt + 1));
        }
        // MeasureAsync records this failure while its normal finally still retires the native renderer.
        throw new InvalidOperationException("Could not record the active native renderer identity after three attempts.", lastError);
    }

    private sealed record Options(string Output, FilamentBackend Backend, string Model, string? File, int HotRuns);
    private sealed class State(Options options)
    {
        public Options Options { get; } = options;
        public List<object> Runs { get; } = [];
        public bool Completed;
        public string? Failure, ModelSha256, NativeLibrarySha256;
        public object? Screenshot, Viewport;
        public Action? RequestStop;
    }
}
