using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeyMelody.Diagnostics;
using HeyMelody.Performance;
using HeyMelodyWindows.Host;
using HeyMelodyWindows.Ui;

namespace HeyMelodyWindows.App;

/// <summary>Explicit diagnostics in the actual published executable, without device I/O.</summary>
internal static class PerformanceBenchmark
{
    private static BenchmarkOptions _options = null!;
    private static BenchmarkReport _report = null!;
    private static readonly Stopwatch Started = Stopwatch.StartNew();
    private static ClientBridge? _bridge;
    private static Process? _host;

    internal static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !BenchmarkOptions.TryParse(args, out var options) || options!.Scenario == "host")
        {
            Console.Error.WriteLine("Usage: --performance-benchmark --run-id <GUID-N> --output-dir <new-absolute-directory> --scenario <ui-static|static-idle|static-scroll|model|model-scroll|integrated-static> [--seconds 0..120] [--warmup-seconds 0..60] [--host-executable <path>] [--host-ready true|false] [--diagnostics true|false]");
            return 2;
        }
        _options = options;
        options.PrepareOutput();
        // These overrides are process-local and are installed before preferences,
        // diagnostics or any application singleton is initialized.
        ThemePreferences.SettingsPathOverride = Path.Combine(options.Output, "data", "preferences.json");
        DiagnosticPaths.DataRootOverride = Path.Combine(options.Output, "data");
        using var diagnostic = options.Diagnostics
            ? IsolatedBenchmarkDiagnostics.Start(DiagnosticComponents.Main, "benchmark", options.RunId, options.Output) : null;
        _report = new()
        {
            RunId = options.RunId, Scenario = options.Scenario, Role = "app",
            WarmupSeconds = options.WarmupSeconds,
            DiagnosticsEnabled = options.Diagnostics,
            SimulatedWorkerCount = options.Scenario == "integrated-static" ? 1 : 0
        };
        using var singleton = HeyMelody.Platform.DesktopRuntime.CreateMutex(options.MainMutex, out _);
        if (!singleton.WaitOne(0)) return 2;
        try
        {
            if (options.Scenario == "integrated-static")
            {
                _bridge = new ClientBridge(options.PipeName, options.HostReady ? null : LaunchHost,
                    backendRunning: () => options.HostReady || _host is { HasExited: false });
                _bridge.BeginConnect();
            }
            ModelRenderingPreferences.Reload();
            ModelRenderingPreferences.Apply(false, ModelFrameRate.Unlimited);
            return AppBuilder.Configure<BenchmarkApplication>().UsePlatformDetect().UseHeyMelodyTypography()
                .UseHeyMelodyRendering(basicRenderMode: false)
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception error)
        {
            _report.Success = false; _report.Failure = error.ToString();
            if (!File.Exists(Path.Combine(options.Output, "benchmark.json"))) _report.Write(options.Output);
            return 1;
        }
        finally
        {
            singleton.ReleaseMutex();
            // The controller stops the isolated Host after observing all App receipts.
            _host?.Dispose();
        }
    }

    private static void LaunchHost()
    {
        var hostOutput = Path.Combine(_options.Output, "host");
        var start = new ProcessStartInfo(_options.HostExecutable!)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(_options.HostExecutable!)! };
        foreach (var argument in new[] { "--performance-benchmark", "--run-id", _options.RunId, "--output-dir", hostOutput,
                     "--scenario", "host", "--seconds", "0", "--diagnostics", _options.Diagnostics ? "true" : "false" })
            start.ArgumentList.Add(argument);
        _host = Process.Start(start) ?? throw new IOException("Benchmark Host could not start.");
    }

    public sealed class BenchmarkApplication : App
    {
        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
            ApplyTheme("light");
            var window = PerformanceBenchmarkScene.Create(_bridge ?? (IHeadsetService)new UnsupportedPlatformService(),
                _options.Scenario.StartsWith("model", StringComparison.Ordinal), _bridge is not null);
            desktop.MainWindow = window;
            window.Opened += (_, _) => _ = MeasureAsync(window, desktop);
            _ = WatchdogAsync(desktop);
        }
    }

    private static async Task MeasureAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        DeviceModelView? model = null;
        try
        {
            var page = window.FindControl<MainView>("Page")!;
            using var readyBudget = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            if (_bridge is not null)
            {
                // The window owns the production subscribe/enumerate/selection path.
                while (page.DataContext is not MainViewModel { HasVerifiedSnapshot: true })
                    await Task.Delay(10, readyBudget.Token);
            }
            else
            {
                while (page.DataContext is not MainViewModel { IsDemoSelected: true })
                    await Task.Delay(10, readyBudget.Token);
            }
            if (_options.Scenario.StartsWith("model", StringComparison.Ordinal))
            {
                while ((model = page.GetVisualDescendants().OfType<DeviceModelView>().FirstOrDefault()) is not { ModelReady: true })
                {
                    if (model?.LoadError is { } failure) throw new InvalidOperationException(failure);
                    await Task.Delay(10, readyBudget.Token);
                }
                _report.ModelSha256 = model.ModelDigest;
                _report.ActualModelBackend = model.Diagnostics.ActualBackend.ToString();
            }
            await NextFrameAsync(window, readyBudget.Token);
            _report.ReadyMilliseconds = Started.Elapsed.TotalMilliseconds;
            _report.WindowWidth = window.Bounds.Width; _report.WindowHeight = window.Bounds.Height;
            _report.RenderScaling = window.RenderScaling;
            _report.Process = BenchmarkProcess.Capture("app", includeHash: false);
            DiagnosticLog.Info("Benchmark", "benchmark.ready", "Isolated product window reached its ready callback.",
                DiagnosticData.Object(("scenario", _options.Scenario), ("readyMilliseconds", _report.ReadyMilliseconds)));
            _report.Success = true;
            _report.Write(_options.Output, "ready.json");
            if (_options.Seconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.WarmupSeconds));
                var timer = Stopwatch.StartNew();
                double? lastCallback = null, lastCopy = null;
                if (model is not null) model.FrameCopiedObserver = _ =>
                {
                    var now = timer.Elapsed.TotalMilliseconds;
                    if (lastCopy is { } previous) _report.BitmapCopyIntervalsMilliseconds.Add(now - previous);
                    lastCopy = now;
                };
                var scroll = page.GetVisualDescendants().OfType<ScrollViewer>()
                    .OrderByDescending(view => view.Extent.Height - view.Viewport.Height).FirstOrDefault();
                var animate = _options.Scenario is not ("static-idle" or "ui-static" or "integrated-static");
                var callbacksDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (animate) window.RequestAnimationFrame(OnFrame);
                while (timer.Elapsed.TotalSeconds < _options.Seconds)
                {
                    _report.Samples.Add(BenchmarkSample.Capture(timer));
                    await Task.Delay(500);
                }
                if (animate) await callbacksDone.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (model is not null) model.FrameCopiedObserver = null;
                _report.MeasurementSeconds = timer.Elapsed.TotalSeconds;
                _report.Samples.Add(BenchmarkSample.Capture(timer));
                void OnFrame(TimeSpan _)
                {
                    var now = timer.Elapsed.TotalMilliseconds;
                    if (lastCallback is { } previous) _report.UiCallbackIntervalsMilliseconds.Add(now - previous);
                    lastCallback = now;
                    if (_options.Scenario.EndsWith("-scroll", StringComparison.Ordinal) && scroll is not null)
                    {
                        var maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                        var phase = timer.Elapsed.TotalSeconds % 4 / 2;
                        scroll.Offset = new(0, maximum * (phase <= 1 ? phase : 2 - phase));
                    }
                    if (timer.Elapsed.TotalSeconds < _options.Seconds) window.RequestAnimationFrame(OnFrame);
                    else callbacksDone.TrySetResult();
                }
            }
        }
        catch (Exception error) { _report.Success = false; _report.Failure = error.ToString(); }
        finally
        {
            if (model is not null) model.FrameCopiedObserver = null;
            _report.Process ??= BenchmarkProcess.Capture("app", includeHash: false);
            _report.Write(_options.Output);
            window.Close();
            desktop.Shutdown(_report.Success ? 0 : 1);
        }
    }

    private static async Task NextFrameAsync(Window window, CancellationToken token)
    {
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.RequestAnimationFrame(_ => frame.TrySetResult());
        await frame.Task.WaitAsync(token);
    }

    private static async Task WatchdogAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Delay(TimeSpan.FromSeconds(_options.Seconds + _options.WarmupSeconds + 60));
        if (!File.Exists(Path.Combine(_options.Output, "benchmark.json")))
        {
            _report.Success = false; _report.Failure = "Published benchmark watchdog expired.";
            _report.Write(_options.Output);
            desktop.Shutdown(1);
        }
    }
}
