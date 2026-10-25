using HeyMelody.Platform;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;
using HeyMelodyWindows.Resources;

internal static class SupervisorDiagnosticsTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        foreach (bool notifications in new[] { false, true })
        {
            await test(notifications ? "initial notification failure survives worker retirement without restoring permissions"
                : "initial GET failure response survives worker retirement without requiring a snapshot event", async () =>
            {
                string reason = notifications ? "查询 0x0200 失败（等待回执超时）" : "查询 0x0103 失败（等待回执超时）";
                await using var fixture = await Fixture.Create(scratch, _ => new DiagnosticPeer(true, reason)
                    { EmitReadyBeforeFailure = notifications, EmitFailureBeforeResponse = notifications });
                await Until(() => fixture.Snapshot.MonitoringState == MonitoringState.Unavailable);
                var failed = fixture.Snapshot;
                Check(failed.Failure == reason && failed.DeviceGeneration > fixture.Current.Generation,
                    "Failure was lost or attached to the retired connection generation.");
                Check(NoWrites(failed) && fixture.Current.Disposals == 1,
                    "Retiring a failed worker restored its write permissions.");
                var result = await fixture.Backend.ApplyAsync(fixture.Id, failed.DeviceGeneration, new("GameMode", 1), default);
                Check(result.FailureKind == ApplyFailure.StateUnavailable && fixture.Current.Applies == 0,
                    "The retained diagnostic allowed a device write.");
                Check(fixture.Snapshot.Failure == reason, "Failure did not survive another snapshot publication.");
            });
        }
        await test("successful reconnect clears the diagnostic and late retired-worker events cannot restore it", async () =>
        {
            await using var fixture = await Fixture.Create(scratch, index => new DiagnosticPeer(index == 0, "查询 0x0103 失败（等待回执超时）"));
            await Until(() => fixture.Snapshot.MonitoringState == MonitoringState.Unavailable);
            var oldWorker = fixture.Current; var failureGeneration = fixture.Snapshot.DeviceGeneration;
            fixture.Backend.Resume(fixture.Id);
            await Until(() => fixture.Snapshot.ControlReady);
            Check(fixture.Snapshot.Failure is null && fixture.Snapshot.DeviceGeneration >= failureGeneration,
                "Successful initialization retained an old failure.");
            oldWorker.EmitFailure("查询 0x0200 失败（等待回执超时）");
            Check(fixture.Snapshot.ControlReady && fixture.Snapshot.Failure is null,
                "A retired worker contaminated the current generation.");
            var current = fixture.Current;
            fixture.Backend.Pause(fixture.Id);
            Check(fixture.Snapshot.Failure is null && NoWrites(fixture.Snapshot), "Pause retained diagnostic or write permissions.");
            fixture.Backend.Resume(fixture.Id); await Until(() => fixture.Snapshot.ControlReady);
            current.EmitFailure("查询 0x0100 失败（等待回执超时）");
            Check(fixture.Snapshot.Failure is null && fixture.Snapshot.ControlReady && fixture.Workers.All(worker => worker.Applies == 0),
                "A previous connection generation changed the recovered snapshot.");
        });
        await test("blank or wrong-identity failures remain generic after retirement", async () =>
        {
            foreach (int mode in new[] { 0, 1 })
            {
                await using var fixture = await Fixture.Create(scratch, _ => new DiagnosticPeer(true, mode == 0 ? "  " : "查询 0x0103 失败（等待回执超时）")
                    { WrongIdentity = mode == 1 });
                await Until(() => fixture.Snapshot.MonitoringState == MonitoringState.Unavailable);
                Check(fixture.Snapshot.Failure is null && NoWrites(fixture.Snapshot),
                    "Blank or unbound failures escaped into the current device snapshot.");
            }
        });
        await test("OS disconnect retires a diagnostic and old callbacks cannot cross its connection generation", async () =>
        {
            await using var fixture = await Fixture.Create(scratch, index => new DiagnosticPeer(index == 0, "未发现所选配置的 SPP 控制服务"));
            await Until(() => fixture.Snapshot.MonitoringState == MonitoringState.Unavailable);
            var oldWorker = fixture.Current; var failedGeneration = fixture.Snapshot.DeviceGeneration;
            fixture.Backend.ObserveConnection(1, false);
            Check(fixture.Snapshot.Failure is null && fixture.Snapshot.DeviceGeneration > failedGeneration && NoWrites(fixture.Snapshot),
                "OS disconnect retained the failed connection's diagnostic.");
            oldWorker.EmitFailure("查询 0x0103 失败（等待回执超时）");
            Check(fixture.Snapshot.Failure is null, "Late failure crossed the disconnected generation.");
            fixture.Backend.ObserveConnection(1, true); await Until(() => fixture.Snapshot.ControlReady);
            Check(fixture.Snapshot.Failure is null && fixture.Workers.All(worker => worker.Applies == 0),
                "OS reconnect restored a retired failure or submitted a setting.");
        });
        await test("resource settings preserve current diagnostics and retired generations cannot revive them", async () =>
        {
            const string reason = "未发现所选配置的 SPP 控制服务";
            await using var fixture = await Fixture.Create(scratch, index => new DiagnosticPeer(index < 2,
                index == 0 ? reason : null), resources: true);
            await Until(() => fixture.Snapshot.MonitoringState == MonitoringState.Unavailable);
            var oldWorker = fixture.Current; var oldGeneration = fixture.Snapshot.DeviceGeneration;
            await fixture.Backend.SaveResourceSettingsAsync(fixture.Backend.ResourceSettings() with
                { HeyMelodyKey = "updated-key", UseOnlineResources = false }, default);
            Check(fixture.Workers.Count == 1 && fixture.Snapshot.Failure == reason
                && fixture.Snapshot.DeviceGeneration == oldGeneration && NoWrites(fixture.Snapshot),
                "A settings update restarted a worker or erased its current diagnostic");
            fixture.Backend.Resume(fixture.Id);
            await Until(() => fixture.Workers.Count == 2 && fixture.Snapshot.MonitoringState == MonitoringState.Unavailable);
            Check(fixture.Snapshot.Failure is null && fixture.Snapshot.DeviceGeneration > oldGeneration && NoWrites(fixture.Snapshot),
                "A new generation reused the previous connection diagnostic");
            oldWorker.EmitFailure("查询 0x0103 失败（等待回执超时）");
            Check(fixture.Snapshot.Failure is null, "A retired callback supplied a diagnostic");
            fixture.Backend.Resume(fixture.Id); await Until(() => fixture.Snapshot.ControlReady);
            Check(fixture.Snapshot.Failure is null && fixture.Workers.Count == 3
                && fixture.Workers.All(worker => worker.Applies == 0) && fixture.Handler.Requests == 0,
                "Reconnect revived a historical failure or performed device/network writes");
        });
    }

    private static bool NoWrites(UiSnapshot snapshot) => !snapshot.ControlReady && !snapshot.CanSetGameMode
        && !snapshot.CanSetAnc && !snapshot.CanSetAncMode && !snapshot.CanSetEqualizer && !snapshot.CanSetCustomEqualizer
        && !snapshot.CanSetDualConnection && !snapshot.CanSetSpatial && !snapshot.CanSetHiRes
        && !snapshot.CanFitTest && !snapshot.CanSetupHearing;
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    private static async Task Until(Func<bool> ready)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!ready()) await Task.Delay(10, budget.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly List<DiagnosticPeer> Workers = [];
        internal readonly RejectNetwork Handler = new();
        private readonly HttpClient _http;
        internal BackendSupervisor Backend { get; }
        internal string Id => Backend.Devices().Single().Identity;
        internal UiSnapshot Snapshot => Backend.Select(Id);
        internal DiagnosticPeer Current => Workers[^1];
        private Fixture(string path, Func<int, DiagnosticPeer> workers, ResourceCoordinator? resources, HttpClient http, RejectNetwork handler)
        {
            _http = http; Handler = handler;
            var store = new DeviceWhitelistStore(Path.Combine(path, "devices.json")); store.Save([DeviceWhitelistStore.KeyFor(1)]);
            Backend = new(store, _ => Task.FromResult<IReadOnlyList<BluetoothCandidate>>([new(1, "耳机", true, true, true, 0x404)]),
                () => { var worker = workers(Workers.Count); Workers.Add(worker); return worker; }, new FrozenTime(), resources);
        }
        internal static async Task<Fixture> Create(string scratch, Func<int, DiagnosticPeer> workers, bool resources = false)
        {
            string path = Path.Combine(scratch, "supervisor-diagnostic-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
            var handler = new RejectNetwork(); var http = new HttpClient(handler);
            ResourceCoordinator? coordinator = null;
            if (resources)
            {
                coordinator = new(Path.Combine(path, "resources"), http);
                await coordinator.InitializeAsync();
                await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { UseOnlineResources = false });
            }
            var fixture = new Fixture(path, workers, coordinator, http, handler);
            await fixture.Backend.InitializeAsync(default); return fixture;
        }
        public async ValueTask DisposeAsync() { await Backend.DisposeAsync(); _http.Dispose(); }
    }

    private sealed class DiagnosticPeer(bool fails, string? failure) : IPeerWorker, IResourcePeerWorker
    {
        public event Action<UiSnapshot>? SnapshotChanged;
        public event Action? Unavailable { add { } remove { } }
        internal bool EmitReadyBeforeFailure, EmitFailureBeforeResponse, WrongIdentity;
        internal int Disposals, Applies;
        internal long Generation = 1;
        private UiSnapshot _snapshot = new();
        public void ConfigureResources(string catalogGzip, long revision, long appMarker, string stableKey,
            long generation, string? observedProductId, byte? observedColor) { Generation = generation; }
        public Task<UiSnapshot> InitializeAsync(ulong address, string identity, string name, CancellationToken token)
        {
            _snapshot = new() { Identity = identity, DeviceName = name, Revision = 1,
                Connected = true, ControlReady = true, SupportedModel = true, ProductId = "067C10", CanSetGameMode = true,
                CanSetAncMode = true, CanSetEqualizer = true, GameMode = false };
            if (EmitReadyBeforeFailure) SnapshotChanged?.Invoke(_snapshot);
            if (fails) _snapshot = new() { Identity = WrongIdentity ? "another-device" : identity, DeviceName = name,
                Revision = 2, Connected = false,
                Failure = failure, CanSetGameMode = true, CanSetAncMode = true, CanSetEqualizer = true };
            if (EmitFailureBeforeResponse || !fails) SnapshotChanged?.Invoke(_snapshot);
            return Task.FromResult(_snapshot);
        }
        internal void EmitFailure(string failure) => SnapshotChanged?.Invoke(new() { Identity = _snapshot.Identity,
            Revision = 999, Failure = failure, Connected = false });
        public Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken token) => Task.FromResult(_snapshot);
        public Task<UiSnapshot> SetObservedColorAsync(string productId, byte color, CancellationToken token) => Task.FromResult(_snapshot);
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        { Applies++; throw new Exception("A diagnostic-only snapshot must not reach the device setter."); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; throw new Exception("Offline diagnostic checks must not access the network."); }
    }
    // Suppress automatic retry timers so explicit recovery and lifecycle assertions are deterministic.
    private sealed class FrozenTime : TimeProvider
    {
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new FrozenTimer();
        private sealed class FrozenTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
