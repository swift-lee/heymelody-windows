using HeyMelody.Platform;
using System.Text.Json;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;

// Host work that happens less often than before: device-state writes, per-event serialization, BLE scan demand and the broadcast timers.
internal static class HostEfficiencyTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        await test("device state: an unchanged re-read inside 60 s updates memory but not the file, and flush persists the latest sample", async () =>
        {
            string path = Path.Combine(scratch, "io-unchanged.json"); var store = new DeviceStateStore(path);
            var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero); string key = new('a', 64);
            store.Remember(key, Presentation(start));
            await UntilAsync(() => Persisted(path, key)?.SampleTime == start);
            byte[] original = File.ReadAllBytes(path);
            store.Remember(key, Presentation(start.AddSeconds(10)));
            await Task.Delay(400);
            Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(original), "A timestamp-only change rewrote the file");
            Check(store.Find(key)!.SampleTime == start.AddSeconds(10), "Memory did not take the newer sample");
            store.Remember(key, Presentation(start.AddSeconds(59)));
            await Task.Delay(400);
            Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(original), "59 s after the queued sample still rewrote the file");
            await store.FlushAsync();
            Check(Persisted(path, key)!.SampleTime == start.AddSeconds(59), "Flush did not persist the latest skipped sample");
            await store.FlushAsync();
        });
        await test("device state: 60 s after the queued sample, new content, an earlier sample or a retired key are written without a flush", async () =>
        {
            string path = Path.Combine(scratch, "io-written.json"); var store = new DeviceStateStore(path);
            var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero); string key = new('b', 64);
            store.Remember(key, Presentation(start));
            await UntilAsync(() => Persisted(path, key)?.SampleTime == start);
            store.Remember(key, Presentation(start.AddSeconds(61)));
            await UntilAsync(() => Persisted(path, key)?.SampleTime == start.AddSeconds(61));
            store.Remember(key, Presentation(start.AddSeconds(62), "2.0"));
            await UntilAsync(() => Persisted(path, key)?.Firmware == "2.0");
            store.Remember(key, Presentation(start.AddSeconds(122), "2.0"));
            await UntilAsync(() => Persisted(path, key)?.SampleTime == start.AddSeconds(122));
            store.Remember(key, Presentation(start.AddSeconds(100), "2.0"));
            await UntilAsync(() => Persisted(path, key)?.SampleTime == start.AddSeconds(100));
            // Retiring the entry forgets what was queued, so the same content is written again at once.
            store.RetireOtherProducts(key, "AAAAAA");
            await UntilAsync(() => Persisted(path, key) is null);
            store.Remember(key, Presentation(start.AddSeconds(101), "2.0"));
            await UntilAsync(() => Persisted(path, key)?.SampleTime == start.AddSeconds(101));
        });
        await test("device state: eviction forgets the queued fingerprint of the evicted key", async () =>
        {
            string path = Path.Combine(scratch, "io-evicted.json"); var store = new DeviceStateStore(path);
            var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
            for (int i = 0; i <= 128; i++) store.Remember(i.ToString("x64"), Presentation(start.AddSeconds(i)));
            await store.FlushAsync();
            Check(Persisted(path, 0.ToString("x64")) is null && Persisted(path, 128.ToString("x64")) is not null, "Oldest entry was not evicted");
            store.Remember(0.ToString("x64"), Presentation(start.AddSeconds(129)));
            await UntilAsync(() => Persisted(path, 0.ToString("x64"))?.SampleTime == start.AddSeconds(129));
        });
        await test("one serialization serves every client: shared line, per-event coalescing keys and identical bytes on the wire", async () =>
        {
            var shared = new BackendPipeServer.Outbound(new BackendEvent { Kind = "health", BackendInstanceId = "i", Message = "同一条" });
            var lines = new string[8]; Parallel.For(0, lines.Length, i => lines[i] = shared.Line());
            Check(lines.All(line => ReferenceEquals(line, lines[0])) && Message(lines[0]) == "同一条", "Concurrent readers did not share one serialized line");
            Check(shared.CoalesceKey == "health:" && new BackendPipeServer.Outbound(new BackendEvent { Kind = "requestExit" }).CoalesceKey is null
                && new BackendPipeServer.Outbound(new BackendEvent { Kind = "snapshot", Snapshot = new() { Identity = "x" } }).CoalesceKey == "snapshot:x"
                && new BackendPipeServer.Outbound(new BackendEvent { Kind = "calibration", Calibration = new() { Handle = "h" } }).CoalesceKey == "calibration:h",
                "Coalescing keys changed");
            string directory = Path.Combine(scratch, "io-broadcast"); Directory.CreateDirectory(directory);
            await using var backend = new BackendSupervisor(new DeviceWhitelistStore(Path.Combine(directory, "devices.json")),
                _ => Task.FromResult<IReadOnlyList<BluetoothCandidate>>([]), () => throw new InvalidOperationException());
            string pipeName = "HeyMelody-io-test-" + Guid.NewGuid().ToString("N");
            await using var server = new BackendPipeServer(backend, pipeName); server.Start();
            await using var first = await EnergyTests.RawClient.Connect(pipeName); await using var second = await EnergyTests.RawClient.Connect(pipeName);
            await first.Subscribe(1, "main"); await second.Subscribe(1, "manager");
            backend.ReportHealth("所有窗口收到同样的字节");
            static bool Health(string line) => JsonSerializer.Deserialize<BackendMessage>(line, RpcWire.Json)!.Event?.Kind == "health";
            string a = await first.ReadUntil(Health), b = await second.ReadUntil(Health);
            Check(a == b && Message(a) == "所有窗口收到同样的字节", "Two clients received different lines");
        });
        await test("ScanDemandChanged fires when a present headset becomes allowed, leaves, returns or is unpaired", async () =>
        {
            string directory = Path.Combine(scratch, "scan-demand"); Directory.CreateDirectory(directory);
            var devices = new List<BluetoothCandidate> { Audio(1) };
            await using var backend = new BackendSupervisor(new DeviceWhitelistStore(Path.Combine(directory, "devices.json")),
                _ => Task.FromResult<IReadOnlyList<BluetoothCandidate>>(devices.ToArray()), () => throw new InvalidOperationException());
            int raised = 0; backend.ScanDemandChanged += () => Interlocked.Increment(ref raised);
            await backend.InitializeAsync(default);
            await Task.Delay(100); Check(!backend.HasScanDemand() && Volatile.Read(ref raised) == 0, "A present but unallowed headset created demand");
            Check((await backend.SaveWhitelistAsync([backend.Catalog().Candidates.Single().Identity], default)).Saved, "Whitelist save failed");
            await UntilAsync(() => Volatile.Read(ref raised) == 1); Check(backend.HasScanDemand(), "Allowing a present headset created no demand");
            devices.Clear(); await backend.RefreshCatalogAsync(default);
            await UntilAsync(() => Volatile.Read(ref raised) == 2); Check(!backend.HasScanDemand(), "A headset that left kept the demand");
            devices.Add(Audio(1)); await backend.RefreshCatalogAsync(default);
            await UntilAsync(() => Volatile.Read(ref raised) == 3); Check(backend.HasScanDemand(), "A headset that returned created no demand");
            await backend.RefreshCatalogAsync(default); backend.ObservePairing(2, true); await Task.Delay(100);
            Check(Volatile.Read(ref raised) == 3, "An unchanged demand raised the event again");
            backend.ObservePairing(1, false);
            await UntilAsync(() => Volatile.Read(ref raised) == 4); Check(!backend.HasScanDemand(), "Unpairing kept the demand");
        });
        await test("broadcast expiry re-arms one timer per peer instead of creating one per observation", async () =>
        {
            string directory = Path.Combine(scratch, "broadcast-timer"); Directory.CreateDirectory(directory);
            var store = new DeviceWhitelistStore(Path.Combine(directory, "devices.json")); store.Save([DeviceWhitelistStore.KeyFor(1)]);
            var time = new BackendTests.TestTime(); var devices = new List<BluetoothCandidate> { Audio(1) };
            await using var backend = new BackendSupervisor(store, _ => Task.FromResult<IReadOnlyList<BluetoothCandidate>>(devices), () => throw new InvalidOperationException(), time);
            await backend.InitializeAsync(default); var id = backend.Devices().Single().Identity;
            int baseline = time.Created;
            BroadcastObservation Observation() => new("06EC10", CaseLidState.Open, EarbudPlacement.InCase, EarbudPlacement.OutOfCase, 20, 40, 60, null, null, null, time.GetUtcNow());
            for (int i = 0; i < 4; i++)
            {
                backend.ObserveBroadcast(1, Observation());
                Check(time.Created - baseline == 1 && time.Armed == 1, $"Observation {i + 1} used {time.Created - baseline} timers, {time.Armed} armed");
                time.Advance(TimeSpan.FromSeconds(10));
                Check(backend.Select(id).ObservationFresh, "A superseded deadline expired the observation");
            }
            // The last observation was at 30 s, so the deadline is 60 s: still fresh at 59 s, gone at 61 s.
            time.Advance(TimeSpan.FromSeconds(19)); Check(backend.Select(id).ObservationFresh, "Expired before its deadline");
            time.Advance(TimeSpan.FromSeconds(2)); await UntilAsync(() => !backend.Select(id).ObservationFresh);
            Check(time.Created - baseline == 1 && time.Armed == 0, "Expiry created a timer or left one armed");
            backend.ObserveBroadcast(1, Observation()); Check(time.Created - baseline == 1 && time.Armed == 1, "A later observation did not reuse the timer");
            await backend.DisposeAsync(); Check(time.Armed == 0, "Disposal left the broadcast timer armed");
        });
    }

    private static string? Message(string line) => JsonSerializer.Deserialize<BackendMessage>(line, RpcWire.Json)!.Event?.Message;
    private static DevicePresentation Presentation(DateTimeOffset sample, string? firmware = null) =>
        new() { ProductId = "06EC10", SampleTime = sample, Firmware = firmware };
    // A second store reads what is on disk, never what the first holds in memory.
    private static DevicePresentation? Persisted(string path, string key) => new DeviceStateStore(path).Find(key);
    private static BluetoothCandidate Audio(ulong address) => new(address, "耳机 " + address, false, true, true, 0x404);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(10, budget.Token);
    }
}
