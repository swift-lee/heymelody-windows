using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;
using HeyMelodyWindows.Windows;

internal static class BackendTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        await test("backend identities remain stable across fresh catalogs and every allowed connected peer is monitored", async () =>
        {
            var devices = new List<BluetoothCandidate> { Audio(1, true), Audio(2, true), Audio(3, false) };
            var store = Store("all"); store.Save(devices.Select(d => DeviceWhitelistStore.KeyFor(d.Address)));
            var workers = new List<FakePeer>();
            await using var backend = New(store, devices, () => { var worker = new FakePeer(); workers.Add(worker); return worker; });
            await backend.InitializeAsync(default);
            await Until(() => backend.Subscribe().Snapshots.Count(s => s.ControlReady) == 2);
            var ids = backend.Devices().Select(d => d.Identity).ToArray();
            await backend.RefreshCatalogAsync(default);
            Check(backend.Devices().Select(d => d.Identity).SequenceEqual(ids));
            Check(workers.Count == 2 && workers.Select(w => w.Address).Order().SequenceEqual(new ulong[] { 1, 2 }));
            backend.Select(ids[0]); backend.Select(ids[1]); Check(workers.Count == 2);
        });
        await test("paused peer stays paused across catalog refresh disconnect reconnect and only resume restarts its worker", async () =>
        {
            var devices = new List<BluetoothCandidate> { Audio(1, true) };
            var store = Store("pause"); store.Save([DeviceWhitelistStore.KeyFor(1)]);
            var workers = new List<FakePeer>();
            await using var backend = New(store, devices, () => { var w = new FakePeer(); workers.Add(w); return w; });
            await backend.InitializeAsync(default); var id = backend.Devices().Single().Identity;
            await Until(() => backend.Select(id).ControlReady);
            backend.Pause(id); backend.ObserveConnection(1, false); backend.ObserveConnection(1, true);
            await backend.RefreshCatalogAsync(default);
            Check(backend.Select(id).MonitoringState == MonitoringState.Paused && workers.Count == 1
                && !backend.Select(id).ControlReady && !backend.Select(id).CanSetAncMode);
            backend.Resume(id); await Until(() => backend.Select(id).ControlReady);
            Check(workers.Count == 2);
        });
        await test("a hanging native child is isolated from another device and times out without a GUI", async () =>
        {
            var devices = new List<BluetoothCandidate> { Audio(1, true), Audio(2, true) };
            var store = Store("hang"); store.Save(devices.Select(d => DeviceWhitelistStore.KeyFor(d.Address)));
            // A first launch on a fresh CI runner (JIT, malware scan) can exceed a sub-second budget by itself.
            await WarmFakePeerAsync();
            await using var backend = New(store, devices, () => new PeerWorker(() => FakeStartInfo(), TimeSpan.FromSeconds(3)));
            await backend.InitializeAsync(default);
            var ids = backend.Devices(); var first = ids[0].Identity; var second = ids[1].Identity;
            await Until(() => backend.Select(second).ControlReady);
            Check(!backend.Select(first).ControlReady && backend.Select(second).MonitoringState == MonitoringState.Monitoring);
            await Until(() => backend.Select(first).MonitoringState == MonitoringState.Unavailable);
            Check(backend.Select(second).ControlReady);
        });
        await test("client EOF cancels queued writes while the already started setting continues in the backend", async () =>
        {
            var devices = new List<BluetoothCandidate> { Audio(1, true) };
            var store = Store("queued"); store.Save([DeviceWhitelistStore.KeyFor(1)]);
            var worker = new FakePeer { HoldApply = true };
            await using var backend = New(store, devices, () => worker);
            await backend.InitializeAsync(default); var id = backend.Devices().Single().Identity;
            await Until(() => backend.Select(id).ControlReady);
            var generation = backend.Select(id).DeviceGeneration;
            using var firstClient = new CancellationTokenSource(); using var secondClient = new CancellationTokenSource();
            var started = backend.ApplyAsync(id, generation, new("GameMode", 1), firstClient.Token);
            await worker.ApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var queued = backend.ApplyAsync(id, generation, new("GameMode", 0), secondClient.Token);
            firstClient.Cancel(); secondClient.Cancel(); worker.ApplyRelease.TrySetResult();
            var receipt = await started;
            try { await queued; throw new InvalidOperationException("Queued write did not cancel."); } catch (OperationCanceledException) { }
            Check(worker.Applies == 1 && receipt.BackendReceiptReceived && receipt.ReadbackMatches == true);
            Check(backend.Select(id).GameMode == true);
        });
        await test("BLE observations display before control readiness stay read-only deduplicate and expire without closing the lid", async () =>
        {
            var devices = new List<BluetoothCandidate> { Audio(1, false) };
            var store = Store("ble"); store.Save([DeviceWhitelistStore.KeyFor(1)]);
            var time = new TestTime(); var worker = new FakePeer();
            await using var backend = new BackendSupervisor(store, _ => Task.FromResult<IReadOnlyList<BluetoothCandidate>>(devices), () => worker, time);
            var opened = 0; backend.EventPublished += e => { if (e.Kind == "caseOpen") opened++; };
            await backend.InitializeAsync(default); var id = backend.Devices().Single().Identity;
            var observation = new BroadcastObservation("06EC10", CaseLidState.Open, EarbudPlacement.InCase,
                EarbudPlacement.OutOfCase, 20, 40, 60, null, null, null, time.GetUtcNow());
            backend.ObserveBroadcast(999, observation); Check(backend.Select(id).LeftBattery is null);
            backend.ObserveBroadcast(1, observation with { ProductId = "000000" }); Check(backend.Select(id).LeftBattery is null);
            backend.ObserveBroadcast(1, observation);
            var snapshot = backend.Select(id); var revision = snapshot.Revision;
            Check(snapshot.LeftBattery == 20 && snapshot.CaseLid == CaseLidState.Open && !snapshot.ControlReady && !snapshot.CanSetGameMode);
            time.Advance(TimeSpan.FromSeconds(10)); backend.ObserveBroadcast(1, observation with { ReceivedAt = time.GetUtcNow() });
            Check(opened == 1 && backend.Select(id).Revision == revision);
            time.Advance(TimeSpan.FromSeconds(31)); await Until(() => !backend.Select(id).ObservationFresh);
            Check(backend.Select(id).CaseLid == CaseLidState.Unknown && backend.Select(id).LeftBattery is null);
            backend.ObserveBroadcast(1, observation with { ReceivedAt = time.GetUtcNow() });
            Check(opened == 1);
            backend.ObserveConnection(1, true); await Until(() => backend.Select(id).ControlReady);
            Check(backend.Select(id).LeftBattery == 20 && backend.Select(id).CaseBattery == 60);
            Check(backend.Select(id).ObservationSource == "spp+ble");
            backend.Pause(id); backend.ObserveBroadcast(1, observation with { LeftBattery = 30, ReceivedAt = time.GetUtcNow() });
            Check(!backend.Select(id).ControlReady && backend.Select(id).LeftBattery == 30);
        });
        await test("SPP and BLE merge each component by its real observation time and only explicit close rearms case open", async () =>
        {
            var devices = new List<BluetoothCandidate> { Audio(1, true) };
            var store = Store("fusion"); store.Save([DeviceWhitelistStore.KeyFor(1)]);
            var time = new TestTime(); var worker = new FakePeer { InitialTime = time.GetUtcNow(), InitialLid = CaseLidState.Closed };
            await using var backend = new BackendSupervisor(store, _ => Task.FromResult<IReadOnlyList<BluetoothCandidate>>(devices), () => worker, time);
            var opened = 0; backend.EventPublished += e => { if (e.Kind == "caseOpen") opened++; };
            await backend.InitializeAsync(default); var id = backend.Devices().Single().Identity;
            await Until(() => backend.Select(id).ControlReady);
            time.Advance(TimeSpan.FromSeconds(1));
            var advertisement = new BroadcastObservation("06EC10", CaseLidState.Open, EarbudPlacement.InCase,
                EarbudPlacement.OutOfCase, 20, 40, 60, null, null, null, time.GetUtcNow());
            backend.ObserveBroadcast(1, advertisement);
            Check(backend.Select(id).LeftBattery == 20 && backend.Select(id).CaseLid == CaseLidState.Open && opened == 1);
            time.Advance(TimeSpan.FromSeconds(1));
            worker.Emit(s => { s.GameMode = true; s.ObservationTime = time.GetUtcNow(); });
            Check(backend.Select(id).LeftBattery == 20 && backend.Select(id).CaseLid == CaseLidState.Open);
            time.Advance(TimeSpan.FromSeconds(1));
            worker.Emit(s => { s.LeftBattery = 95; s.LeftBatteryObservationTime = time.GetUtcNow(); });
            Check(backend.Select(id).LeftBattery == 95 && backend.Select(id).RightBattery == 40 && backend.Select(id).CaseLid == CaseLidState.Open);
            time.Advance(TimeSpan.FromSeconds(31));
            await Until(() => backend.Select(id).CaseLid == CaseLidState.Closed);
            backend.ObserveBroadcast(1, advertisement with { ReceivedAt = time.GetUtcNow() });
            Check(opened == 1); // Revealing a retained, older SPP close is not a new closing event.
            time.Advance(TimeSpan.FromSeconds(1));
            worker.Emit(s => { s.CaseLid = CaseLidState.Closed; s.CaseObservationTime = time.GetUtcNow(); });
            time.Advance(TimeSpan.FromSeconds(1));
            backend.ObserveBroadcast(1, advertisement with { ReceivedAt = time.GetUtcNow() });
            Check(opened == 2);
            backend.SetSuspended(true); backend.SetSuspended(false);
            await Until(() => backend.Select(id).ControlReady);
            time.Advance(TimeSpan.FromSeconds(1));
            backend.ObserveBroadcast(1, advertisement with { ReceivedAt = time.GetUtcNow() }); Check(opened == 2);
            worker.Emit(s => { s.SupportedModel = false; s.ProductId = "000001"; });
            Check(backend.Select(id).CaseBattery is null && backend.Select(id).ObservationSource != "spp+ble");
            backend.ObserveBroadcast(1, advertisement with { LeftBattery = 1, ReceivedAt = time.GetUtcNow() });
            Check(backend.Select(id).LeftBattery != 1);
        });
        await test("partial BLE packets retain components only until each original freshness deadline and reject previous scan packets", async () =>
        {
            var devices = new List<BluetoothCandidate> { Audio(1, false) };
            var store = Store("partial-ble"); store.Save([DeviceWhitelistStore.KeyFor(1)]);
            var time = new TestTime();
            await using var backend = new BackendSupervisor(store, _ => Task.FromResult<IReadOnlyList<BluetoothCandidate>>(devices), () => new FakePeer(), time);
            await backend.InitializeAsync(default); var id = backend.Devices().Single().Identity;
            var initial = new BroadcastObservation("06EC10", CaseLidState.Closed, EarbudPlacement.InCase,
                EarbudPlacement.InCase, 20, 40, 60, null, null, null, time.GetUtcNow());
            backend.ObserveBroadcast(1, initial);
            time.Advance(TimeSpan.FromSeconds(10));
            backend.ObserveBroadcast(1, initial with { Lid = CaseLidState.Open, Left = EarbudPlacement.Unknown, Right = EarbudPlacement.Unknown,
                LeftBattery = null, RightBattery = null, CaseBattery = null, ReceivedAt = time.GetUtcNow() });
            Check(backend.Select(id).LeftBattery == 20 && backend.Select(id).CaseLid == CaseLidState.Open);
            time.Advance(TimeSpan.FromSeconds(21));
            await Until(() => backend.Select(id).LeftBattery is null);
            Check(backend.Select(id).CaseLid == CaseLidState.Open && backend.Select(id).ObservationFresh);
            time.Advance(TimeSpan.FromSeconds(10)); await Until(() => !backend.Select(id).ObservationFresh);
            var epoch = DateTimeOffset.UtcNow;
            Check(!WindowsStateMonitor.IsCurrentAdvertisement(epoch.AddMilliseconds(-1), epoch, -30));
            Check(!WindowsStateMonitor.IsCurrentAdvertisement(epoch, epoch, -127));
            Check(WindowsStateMonitor.IsCurrentAdvertisement(epoch, epoch, -30));
        });
        await test("unsupported SPP initialization discards an earlier supported-product BLE cache for the same peer", async () =>
        {
            var devices = new List<BluetoothCandidate> { Audio(1, false) };
            var store = Store("ble-product-conflict"); store.Save([DeviceWhitelistStore.KeyFor(1)]);
            var worker = new FakePeer { InitialProduct = "000001", InitialReady = false, InitialSupported = false };
            await using var backend = New(store, devices, () => worker);
            await backend.InitializeAsync(default); var id = backend.Devices().Single().Identity;
            var broadcast = new BroadcastObservation("06EC10", CaseLidState.Open, EarbudPlacement.InCase,
                EarbudPlacement.InCase, 10, 20, 30, null, null, null, DateTimeOffset.UtcNow);
            backend.ObserveBroadcast(1, broadcast); Check(backend.Select(id).CaseBattery == 30);
            backend.ObserveConnection(1, true);
            await Until(() => backend.Select(id).MonitoringState == MonitoringState.Unsupported);
            Check(backend.Select(id).CaseBattery is null && backend.Select(id).ProductId == "000001"
                && backend.Select(id).ObservationSource is null && !backend.Select(id).ControlReady);
            backend.ObserveBroadcast(1, broadcast with { ReceivedAt = DateTimeOffset.UtcNow });
            Check(backend.Select(id).CaseBattery is null);
        });
        await test("radio off and suspend invalidate live state preserve pause and recover connected peers only", async () =>
        {
            var devices = new List<BluetoothCandidate> { Audio(1, true), Audio(2, true) };
            var store = Store("power"); store.Save(devices.Select(d => DeviceWhitelistStore.KeyFor(d.Address)));
            await using var backend = New(store, devices, () => new FakePeer());
            await backend.InitializeAsync(default); var ids = backend.Devices().Select(d => d.Identity).ToArray();
            await Until(() => backend.Select(ids[0]).ControlReady);
            backend.Pause(ids[0]); backend.SetSuspended(true); backend.SetRadioAvailable(false);
            Check(backend.Subscribe().Snapshots.All(s => !s.ControlReady && !s.CanSetAncMode));
            backend.SetRadioAvailable(true); backend.SetSuspended(false);
            await Until(() => backend.Select(ids[1]).ControlReady);
            Check(backend.Select(ids[1]).CanSetAncMode && !backend.Select(ids[0]).CanSetAncMode);
            Check(backend.Select(ids[0]).MonitoringState == MonitoringState.Paused);
        });
        await test("named pipe has an atomic subscription baseline version errors do not kill backend and exit can be vetoed", async () =>
        {
            var devices = new List<BluetoothCandidate> { Audio(1, false) };
            var store = Store("pipe"); store.Save([DeviceWhitelistStore.KeyFor(1)]);
            await using var backend = New(store, devices, () => new FakePeer());
            await backend.InitializeAsync(default);
            var pipeName = "HeyMelody-host-tests-" + Guid.NewGuid().ToString("N");
            await using var server = new BackendPipeServer(backend, pipeName); server.Start();
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(3000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            var lines = new RpcLineReader(pipe);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new BackendRequest { Id = 1, Operation = "subscribe", Role = "main" }, RpcWire.Json));
            var baseline = await Read(); Check(baseline.Kind == "response" && baseline.Success);
            var initial = baseline.Data!.Value.Deserialize<BackendSubscription>(RpcWire.Json)!;
            Check(initial.BackendInstanceId == backend.InstanceId && initial.Snapshots.Count == 1);
            var snapshotId = initial.Snapshots.Single().Identity;
            backend.ObserveBroadcast(1, new("06EC10", CaseLidState.Closed, EarbudPlacement.InCase, EarbudPlacement.InCase, 10, 20, 30, null, null, null, DateTimeOffset.UtcNow));
            var update = await ReadUntil(m => m.Event?.Kind == "snapshot");
            Check(update.Event!.Snapshot!.Revision > initial.Snapshots[0].Revision && update.Event.Snapshot.Identity == snapshotId);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new BackendRequest { SchemaVersion = 1, Id = 2, Operation = "enumerate" }, RpcWire.Json));
            var mismatch = await ReadUntil(m => m.Id == 2); Check(mismatch.ErrorCode == "versionMismatch");
            var exiting = server.RequestExitAsync();
            var requestExit = await ReadUntil(m => m.Event?.Kind == "requestExit");
            await writer.WriteLineAsync(JsonSerializer.Serialize(new BackendRequest { Id = 3, Operation = "exitVote", BackendInstanceId = backend.InstanceId, ExitRequestId = requestExit.Event!.ExitRequestId, Approved = false }, RpcWire.Json));
            Check((await ReadUntil(m => m.Id == 3)).Success);
            await ReadUntil(m => m.Event?.Kind == "exitCancelled"); await exiting;
            Check(backend.Select(snapshotId).Identity == snapshotId);
            exiting = server.RequestExitAsync(); requestExit = await ReadUntil(m => m.Event?.Kind == "requestExit");
            await writer.WriteLineAsync(JsonSerializer.Serialize(new BackendRequest { Id = 4, Operation = "exitVote", BackendInstanceId = backend.InstanceId, ExitRequestId = requestExit.Event!.ExitRequestId, Approved = true }, RpcWire.Json));
            Check((await Read()).Id == 4); var stopping = (await Read()).Event; Check(stopping?.Kind == "stopping");
            await writer.WriteLineAsync(JsonSerializer.Serialize(new BackendRequest { Id = 5, Operation = "stoppingAck", BackendInstanceId = backend.InstanceId, ExitRequestId = stopping!.ExitRequestId }, RpcWire.Json));
            await exiting;
            async Task<BackendMessage> Read() => JsonSerializer.Deserialize<BackendMessage>(
                await lines.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) ?? throw new IOException(), RpcWire.Json)!;
            async Task<BackendMessage> ReadUntil(Func<BackendMessage, bool> match)
            { for (int i = 0; i < 20; i++) { var message = await Read(); if (match(message)) return message; } throw new InvalidOperationException(); }
        });

        await test("a client lookup joins the tray's start-up catalog refresh instead of queueing a second catalog worker", async () =>
        {
            IReadOnlyList<BluetoothCandidate> devices = [Audio(1, false)];
            var store = Store("startup-join"); store.Save([DeviceWhitelistStore.KeyFor(1)]);
            int calls = 0; var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var backend = new BackendSupervisor(store, async token =>
            { Interlocked.Increment(ref calls); await release.Task.WaitAsync(token); return devices; }, () => new FakePeer());
            var startup = backend.InitializeAsync(default);
            await Until(() => Volatile.Read(ref calls) == 1);
            using (var abandoned = new CancellationTokenSource())
            {
                var cancelled = backend.RefreshCatalogForClientAsync(abandoned.Token);
                abandoned.Cancel();
                try { await cancelled; Check(false); } catch (OperationCanceledException) { }
            }
            var joined = backend.RefreshCatalogForClientAsync(default);
            await Task.Delay(50);
            Check(!joined.IsCompleted && !startup.IsCompleted);
            release.SetResult();
            await startup; await joined.WaitAsync(TimeSpan.FromSeconds(3));
            Check(calls == 1 && backend.Devices().Count == 1);
            // Outside start-up every client lookup is a fresh refresh, as before.
            await backend.RefreshCatalogForClientAsync(default);
            Check(calls == 2);
        });
        await test("a failed start-up catalog refresh makes a joined client lookup run its own", async () =>
        {
            IReadOnlyList<BluetoothCandidate> devices = [Audio(1, false)];
            var store = Store("startup-fail"); store.Save([DeviceWhitelistStore.KeyFor(1)]);
            int calls = 0; var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var backend = new BackendSupervisor(store, async token =>
            {
                if (Interlocked.Increment(ref calls) == 1) { await release.Task.WaitAsync(token); throw new IOException(); }
                return devices;
            }, () => new FakePeer());
            var startup = backend.InitializeAsync(default);
            await Until(() => Volatile.Read(ref calls) == 1);
            var joined = backend.RefreshCatalogForClientAsync(default);
            release.SetResult();
            try { await startup; Check(false); } catch (IOException) { }
            await joined.WaitAsync(TimeSpan.FromSeconds(3));
            Check(calls == 2 && backend.Devices().Count == 1);
        });

        DeviceWhitelistStore Store(string name) => new(Path.Combine(scratch, "backend-" + name + ".json"));
        static BackendSupervisor New(DeviceWhitelistStore store, IReadOnlyList<BluetoothCandidate> devices, Func<IPeerWorker> workers)
            => new(store, _ => Task.FromResult(devices), workers);
    }
    private static BluetoothCandidate Audio(ulong address, bool connected) => new(address, "耳机 " + address, connected, true, true, 0x404);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Backend assertion failed."); }
    private static async Task Until(Func<bool> condition)
    { using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(4)); while (!condition()) await Task.Delay(10, budget.Token); }

    private sealed class FakePeer : IPeerWorker
    {
        public event Action<UiSnapshot>? SnapshotChanged;
        public event Action? Unavailable;
        private UiSnapshot _snapshot = new();
        internal ulong Address; internal int Applies; internal bool HoldApply;
        internal DateTimeOffset InitialTime = DateTimeOffset.UtcNow;
        internal CaseLidState InitialLid;
        internal string InitialProduct = "06EC10";
        internal bool InitialReady = true, InitialSupported = true;
        internal readonly TaskCompletionSource ApplyEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ApplyRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<UiSnapshot> InitializeAsync(ulong address, string identity, string name, CancellationToken token)
        {
            Address = address; _snapshot = new() { Identity = identity, DeviceName = name, Revision = 1, Connected = true,
                ConnectionState = ConnectionState.Connected, ControlReady = InitialReady, SupportedModel = InitialSupported,
                CanSetGameMode = InitialReady, CanSetAncMode = InitialReady,
                ProductId = InitialProduct, GameMode = false, LeftBattery = 80, RightBattery = 90,
                LeftBatteryObservationTime = InitialTime, RightBatteryObservationTime = InitialTime,
                CaseLid = InitialLid, CaseObservationTime = InitialLid == CaseLidState.Unknown ? null : InitialTime };
            SnapshotChanged?.Invoke(_snapshot); return Task.FromResult(_snapshot);
        }
        public Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken token) => Task.FromResult(_snapshot);
        internal void Emit(Action<UiSnapshot> change)
        {
            _snapshot = JsonSerializer.Deserialize<UiSnapshot>(JsonSerializer.Serialize(_snapshot, RpcWire.Json), RpcWire.Json)!;
            _snapshot.Revision++; change(_snapshot); SnapshotChanged?.Invoke(_snapshot);
        }
        public async Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        {
            Applies++; ApplyEntered.TrySetResult(); if (HoldApply) await ApplyRelease.Task.WaitAsync(token);
            _snapshot = JsonSerializer.Deserialize<UiSnapshot>(JsonSerializer.Serialize(_snapshot, RpcWire.Json), RpcWire.Json)!;
            _snapshot.Revision++; _snapshot.GameMode = action.Value == 1; SnapshotChanged?.Invoke(_snapshot);
            return new() { BackendReceiptReceived = true, ConnectionState = ConnectionState.Connected, TargetSubmitted = true,
                Accepted = true, ReadbackMatches = true, SubmissionAttempted = true, After = _snapshot };
        }
        public ValueTask DisposeAsync() { Unavailable?.Invoke(); return ValueTask.CompletedTask; }
    }

    internal sealed class TestTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private readonly List<TestTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new TestTimer(this, callback, state); _timers.Add(timer); timer.Change(dueTime, period); return timer; }
        internal int Created => _timers.Count;
        internal int Armed => _timers.Count(timer => !timer.Disposed && timer.Due != DateTimeOffset.MaxValue);
        // Timers are one-shot like the real ones with an infinite period: firing disarms, Change re-arms, Infinite never fires.
        internal void Advance(TimeSpan duration)
        { _now += duration; foreach (var timer in _timers.ToArray()) if (!timer.Disposed && timer.Due <= _now) { timer.Due = DateTimeOffset.MaxValue; timer.Callback(timer.State); } }
        private sealed class TestTimer(TestTime owner, TimerCallback callback, object? state) : ITimer
        {
            internal DateTimeOffset Due; internal bool Disposed;
            internal TimerCallback Callback = callback; internal object? State = state;
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner._now + dueTime; return true; }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private static ProcessStartInfo FakeStartInfo()
    {
        var path = Environment.ProcessPath!;
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false) };
        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--fake-peer"); return start;
    }
    private static async Task WarmFakePeerAsync()
    {
        using var process = Process.Start(FakeStartInfo()) ?? throw new IOException();
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }
    internal static async Task<int> FakePeerMainAsync()
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        while (await Console.In.ReadLineAsync() is { } line)
        {
            var request = JsonSerializer.Deserialize<WorkerRequest>(line, RpcWire.Json)!;
            if (request.Address == 1) await Task.Delay(Timeout.Infinite);
            var snapshot = new UiSnapshot { Identity = request.Identity, DeviceName = request.Name, ControlReady = true,
                Connected = true, SupportedModel = true, ConnectionState = ConnectionState.Connected, Revision = 1 };
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new WorkerMessage { Id = request.Id, Success = true,
                Data = JsonSerializer.SerializeToElement(snapshot, RpcWire.Json) }, RpcWire.Json)); await Console.Out.FlushAsync();
        }
        return 0;
    }
}
