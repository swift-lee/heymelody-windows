using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using HeyMelody.Performance;
using HeyMelody.Diagnostics;
using HeyMelody.Platform;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Host;

/// <summary>Production tray/IPC with one isolated stdio worker and no native device services.</summary>
internal static class PerformanceBenchmarkHost
{
    internal static BenchmarkOptions? Options { get; private set; }
    private static readonly Stopwatch Started = Stopwatch.StartNew();
    private const ulong FixtureAddress = 1;

    internal static bool TryConfigure(string[] args, out int exit)
    {
        exit = 2;
        if (!BenchmarkOptions.TryParse(args, out var options) || options!.Scenario != "host") return false;
        options.PrepareOutput();
        Options = options;
        DiagnosticPaths.DataRootOverride = Path.Combine(options.Output, "data");
        exit = 0;
        return true;
    }

    internal static BackendSupervisor CreateSupervisor()
    {
        var options = Options!;
        var whitelist = new DeviceWhitelistStore(Path.Combine(options.Output, "data", "devices.json"));
        whitelist.Save([DeviceWhitelistStore.KeyFor(FixtureAddress)]);
        IReadOnlyList<BluetoothCandidate> devices =
            [new(FixtureAddress, "性能基准模拟耳机", true, true, true, 0x404)];
        return new(whitelist, _ => Task.FromResult(devices),
            () => new PeerWorker(() => StartInfo(options)), locationProvider: HostPlatformFactory.UnavailableLocation());
    }

    private static ProcessStartInfo StartInfo(BenchmarkOptions options)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--performance-benchmark-worker");
        start.ArgumentList.Add(options.Output);
        return start;
    }

    internal static async Task MeasureAsync(BackendSupervisor supervisor, BackendPipeServer server, EnergyPolicy energy,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        var options = Options!;
        var report = new BenchmarkReport
        {
            RunId = options.RunId, Scenario = "host", Role = "tray", RealTray = true,
            SimulatedWorkerCount = 1, WarmupSeconds = options.WarmupSeconds,
            DiagnosticsEnabled = options.Diagnostics,
            Scope = "Real published tray/Avalonia/IPC/EnergyPolicy and one simulated stdio worker; no Bluetooth monitor, location provider, catalog worker or resource downloads."
        };
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await supervisor.InitializeAsync(startup.Token);
            while (!supervisor.Subscribe().Snapshots.Any(snapshot => snapshot.ControlReady))
                await Task.Delay(10, startup.Token);
            report.ReadyMilliseconds = Started.Elapsed.TotalMilliseconds;
            report.Process = BenchmarkProcess.Capture("tray", includeHash: false);
            DiagnosticLog.Info("Benchmark", "benchmark.ready", "Isolated tray and simulated worker reached readiness.",
                DiagnosticData.Object(("readyMilliseconds", report.ReadyMilliseconds), ("simulatedWorkerCount", 1)));
            report.Success = true;
            report.Write(options.Output, "ready.json");
            if (options.Seconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.WarmupSeconds));
                using var idleBudget = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                while (energy.GuiCount == 0 && energy.Mode != EnergyMode.Eco)
                    await Task.Delay(100, idleBudget.Token);
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed.TotalSeconds < options.Seconds && !File.Exists(options.StopFile))
                {
                    report.Samples.Add(BenchmarkSample.Capture(timer, energy.Mode.ToString()));
                    await Task.Delay(500);
                }
                report.MeasurementSeconds = timer.Elapsed.TotalSeconds;
                report.Samples.Add(BenchmarkSample.Capture(timer, energy.Mode.ToString()));
                report.Write(options.Output);
            }
            // The controller owns the lifetime, including zero-second startup-only runs.
            using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            while (!File.Exists(options.StopFile)) await Task.Delay(100, lifetime.Token);
        }
        catch (Exception error)
        {
            report.Success = false; report.Failure = error.ToString();
            if (!File.Exists(Path.Combine(options.Output, "benchmark.json"))) report.Write(options.Output);
        }
        finally
        {
            await server.DisposeAsync();
            await supervisor.DisposeAsync();
            await Dispatcher.UIThread.InvokeAsync(() => desktop.Shutdown(report.Success ? 0 : 1));
        }
    }

    internal static async Task<int> RunWorkerAsync(string output)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        var identity = BenchmarkProcess.Capture("worker", includeHash: false);
        using (var stream = new FileStream(Path.Combine(output, "worker.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            JsonSerializer.Serialize(stream, identity, PerformanceBenchmarkJson.Default.BenchmarkProcess);
        UiSnapshot snapshot = new();
        while (await Console.In.ReadLineAsync() is { } line)
        {
            var request = JsonSerializer.Deserialize(line, HostWire.TypeInfo<WorkerRequest>())!;
            if (request.Operation == "initialize")
                snapshot = new()
                {
                    Identity = request.Identity, DeviceName = request.Name, ProductId = "06EC10",
                    Connected = true, ConnectionState = ConnectionState.Connected, ControlReady = true,
                    SupportedModel = true, Revision = 1, LeftBattery = 80, RightBattery = 90,
                    LeftBatteryObservationTime = DateTimeOffset.UtcNow, RightBatteryObservationTime = DateTimeOffset.UtcNow
                };
            var response = new WorkerMessage
            {
                Id = request.Id, Success = request.Operation is "initialize" or "refresh",
                Data = request.Operation is "initialize" or "refresh"
                    ? JsonSerializer.SerializeToElement(snapshot, RpcWire.TypeInfo<UiSnapshot>()) : null,
                Error = request.Operation is "initialize" or "refresh" ? null : "Benchmark worker refuses all writes."
            };
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, HostWire.TypeInfo<WorkerMessage>()));
            await Console.Out.FlushAsync();
        }
        return 0;
    }
}
