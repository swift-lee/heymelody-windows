using HeyMelody.Platform;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;

internal static class BridgeExitIntegrationTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        await test("client exit RPC replies before votes and uses the shared terminal lifecycle", async () =>
        {
            await using var fixture = new Fixture(scratch);
            await using var main = fixture.Bridge();
            var allowVote = Signal<bool>();
            var stopped = Watch(main, prepare: () => allowVote.Task);
            await main.SubscribeAsync("main", default);
            await main.RequestApplicationExitAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
            await main.RequestApplicationExitAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
            Check(fixture.Approved == 0 && !stopped.IsCompleted);
            allowVote.TrySetResult(true);
            await stopped.WaitAsync(TimeSpan.FromSeconds(4));
            for (int i = 0; fixture.Approved == 0 && i < 50; i++) await Task.Delay(10);
            Check(fixture.Approved == 1 && main.IsStopping && fixture.Launches == 0);
        });

        await test("real bridges for main, manager, calibration and warm startup stop together without launching another backend", async () =>
        {
            await using var fixture = new Fixture(scratch);
            await using var main = fixture.Bridge(); await using var manager = fixture.Bridge();
            await using var calibration = fixture.Bridge(); await using var warm = fixture.Bridge();
            var stops = new[] { Watch(main), Watch(manager), Watch(calibration), Watch(warm, vote: false) };
            await main.SubscribeAsync("main", default); await manager.SubscribeAsync("manager", default);
            await calibration.SubscribeAsync("calibration", default); warm.BeginConnect();
            await fixture.Server.RequestExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(3));
            Check(new[] { main, manager, calibration, warm }.All(bridge => bridge.IsStopping));
            await using var another = fixture.Bridge(); another.BeginConnect();
            try { await another.EnumerateAsync(default); Check(false); } catch (IOException) { }
            Check(another.IsStopping && fixture.Launches == 0 && fixture.Approved == 1);
        });

        await test("a real warm bridge subscribes during prepare, votes as manager, and recovers after cancellation", async () =>
        {
            await using var fixture = new Fixture(scratch);
            await using var main = fixture.Bridge(); await using var manager = fixture.Bridge();
            var mainPrepare = Signal<BackendEvent>(); var managerPrepare = Signal<BackendEvent>();
            var mainCancelled = Signal<BackendEvent>(); var managerCancelled = Signal<BackendEvent>();
            main.EventReceived += value => { if (value.Kind == "requestExit") mainPrepare.TrySetResult(value); if (value.Kind == "exitCancelled") mainCancelled.TrySetResult(value); };
            manager.EventReceived += value => { if (value.Kind == "requestExit") managerPrepare.TrySetResult(value); if (value.Kind == "exitCancelled") managerCancelled.TrySetResult(value); };
            await main.SubscribeAsync("main", default); manager.BeginConnect();
            var exiting = fixture.Server.RequestExitAsync();
            var prepare = await mainPrepare.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!managerPrepare.Task.IsCompleted);
            await manager.SubscribeAsync("manager", default);
            Check((await managerPrepare.Task.WaitAsync(TimeSpan.FromSeconds(3))).ExitRequestId == prepare.ExitRequestId);
            await main.AcknowledgeExitAsync(prepare.ExitRequestId!, true, default);
            await manager.AcknowledgeExitAsync(prepare.ExitRequestId!, false, default);
            await Task.WhenAll(mainCancelled.Task, managerCancelled.Task).WaitAsync(TimeSpan.FromSeconds(3));
            await exiting.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!main.IsStopping && !manager.IsStopping && (await main.EnumerateAsync(default)).Count == 0);
            Check(fixture.Launches == 0 && fixture.Approved == 0);
        });

        await test("fit and hearing bridges may cancel an active calibration on their original pipe before voting yes", async () =>
        {
            foreach (string kind in new[] { "fit", "hearing" })
            {
                var worker = new CalibrationPeer();
                await using var fixture = new Fixture(scratch, worker);
                await fixture.Backend.InitializeAsync(default);
                using var readyBudget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                while (!fixture.Backend.Subscribe().Snapshots.Any(value => value.ControlReady)) await Task.Delay(10, readyBudget.Token);
                await using var bridge = fixture.Bridge();
                var baseline = await bridge.SubscribeAsync("calibration", default);
                var snapshot = baseline.Snapshots.Single();
                var state = await bridge.CreateCalibrationAsync(snapshot.Identity, snapshot.DeviceGeneration, kind, default);
                state = await bridge.AdvanceCalibrationAsync(state.Handle, state.Revision, new("start"), default);
                Check(state.Stage is "FitTesting" or "Measuring");
                var stopped = Watch(bridge, prepare: async () =>
                {
                    var cancelled = await bridge.CancelCalibrationAsync(state.Handle, default);
                    Check(cancelled.Stage == "Cancelled" && worker.Cancels == 1);
                });
                await fixture.Server.RequestExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await stopped.WaitAsync(TimeSpan.FromSeconds(3));
                Check(worker.Cancels == 1 && fixture.Launches == 0 && fixture.Approved == 1);
            }
        });
    }

    private static Task Watch(ClientBridge bridge, bool vote = true, Func<Task>? prepare = null)
    {
        var stopped = Signal<bool>();
        bridge.EventReceived += value =>
        {
            if (value.Kind == "requestExit" && vote) _ = Vote(value);
            if (value.Kind == "stopping") _ = Close();
        };
        return stopped.Task;
        async Task Vote(BackendEvent value)
        {
            try { if (prepare is not null) await prepare(); await bridge.AcknowledgeExitAsync(value.ExitRequestId!, true, default); }
            catch (Exception error) { stopped.TrySetException(error); }
        }
        async Task Close()
        {
            try { await bridge.DisposeAsync(); stopped.TrySetResult(true); }
            catch (Exception error) { stopped.TrySetException(error); }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly BackendSupervisor Backend;
        internal readonly BackendPipeServer Server;
        private readonly string _pipe = "HeyMelody-BridgeExit-" + Guid.NewGuid().ToString("N");
        internal int Launches, Approved;
        internal Fixture(string scratch, CalibrationPeer? worker = null)
        {
            var store = new DeviceWhitelistStore(Path.Combine(scratch, Guid.NewGuid().ToString("N") + ".json"));
            IReadOnlyList<BluetoothCandidate> devices = [];
            if (worker is not null) { store.Save([DeviceWhitelistStore.KeyFor(1)]); devices = [new(1, "Synthetic calibration peer", true, true, true, 0x404)]; }
            Backend = new(store, _ => Task.FromResult(devices), () => worker ?? throw new InvalidOperationException("Unexpected device worker"));
            Server = new(Backend, _pipe, exitVoteBudget: TimeSpan.FromSeconds(3), stoppingBudget: TimeSpan.FromSeconds(2));
            Server.ExitApproved += async () => { Interlocked.Increment(ref Approved); await Server.DisposeAsync(); };
            Server.Start();
        }
        internal ClientBridge Bridge() => new(_pipe, () => Interlocked.Increment(ref Launches), TimeSpan.FromSeconds(4), () => true);
        public async ValueTask DisposeAsync() { await Server.DisposeAsync(); await Backend.DisposeAsync(); }
    }

    private sealed class CalibrationPeer : IPeerWorker, ICalibrationPeerWorker
    {
        public event Action<UiSnapshot>? SnapshotChanged;
        public event Action? Unavailable;
        public event Action<UiCalibrationState>? CalibrationChanged;
        public event Action<UiCalibrationSpectrum>? CalibrationSpectrumChanged { add { } remove { } }
        private UiSnapshot _snapshot = new();
        private UiCalibrationState _state = new();
        internal int Cancels;
        public Task<UiSnapshot> InitializeAsync(ulong address, string identity, string name, CancellationToken token)
        {
            _snapshot = new() { Identity = identity, DeviceName = name, ProductId = "06EC10", Revision = 1, Connected = true,
                ConnectionState = ConnectionState.Connected, ControlReady = true, SupportedModel = true, CanFitTest = true, CanSetupHearing = true };
            SnapshotChanged?.Invoke(_snapshot); return Task.FromResult(_snapshot);
        }
        public Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken token) => Task.FromResult(_snapshot);
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw new InvalidOperationException("SET is forbidden in exit tests");
        public Task<UiCalibrationState> CreateCalibrationAsync(string handle, string kind, CancellationToken token)
        { _state = new() { Handle = handle, Workflow = kind, Stage = "Ready", Revision = 1 }; return Task.FromResult(_state); }
        public Task<UiCalibrationState> GetCalibrationAsync(string handle, CancellationToken token) => Task.FromResult(_state);
        public Task<UiCalibrationState> AdvanceCalibrationAsync(string handle, long revision, UiCalibrationInput input, CancellationToken token)
        { _state = _state with { Revision = _state.Revision + 1, Stage = _state.Workflow == "fit" ? "FitTesting" : "Measuring" }; return Task.FromResult(_state); }
        public Task<UiCalibrationState> CancelCalibrationAsync(string handle, CancellationToken token)
        { Cancels++; _state = _state with { Revision = _state.Revision + 1, Stage = "Cancelled" }; CalibrationChanged?.Invoke(_state); return Task.FromResult(_state); }
        public Task<IReadOnlyList<UiHearingProfile>> HearingProfilesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<UiHearingProfile>>([]);
        public Task<UiApplyResult> SelectHearingAsync(int? uid, CancellationToken token) => throw new InvalidOperationException("SET is forbidden in exit tests");
        public ValueTask DisposeAsync() { Unavailable?.Invoke(); return ValueTask.CompletedTask; }
    }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Bridge exit integration assertion failed"); }
}
