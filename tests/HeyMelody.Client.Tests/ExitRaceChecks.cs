using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HeyMelody.Diagnostics;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;

internal static class ExitRaceChecks
{
    private static readonly string[] Cases = ["warm-stopping-eof", "original-pipe-stopping-ack", "late-sticky-stopping",
        "full-event-queue", "vote-cancel-resume", "vote-never-reconnects", "second-bridge-cannot-relaunch",
        "atomic-cross-bridge-launch", "reconnect-baseline-backpressure", "calibration-state-backpressure",
        "orphan-bound-launch", "unexpected-eof-recovers"];

    internal static async Task<int> RunAsync()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        int selected = Array.IndexOf(arguments, "--case");
        if (selected >= 0)
        {
            if (selected + 1 >= arguments.Length || !Cases.Contains(arguments[selected + 1])) throw new ArgumentException("Unknown isolated exit fixture.");
            await RunCaseAsync(arguments[selected + 1]).WaitAsync(TimeSpan.FromSeconds(12));
            Console.WriteLine("PASS " + arguments[selected + 1]);
            return 1;
        }
        // A controlled exit may be a process-wide terminal state. Each scenario gets a fresh test process.
        string root = Path.Combine(Path.GetTempPath(), "HeyMelody-ExitLifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var results = new List<object>();
        int passed = 0;
        foreach (string name in Cases)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--exit-lifecycle-tests"); start.ArgumentList.Add("--case"); start.ArgumentList.Add(name);
            if (name == "orphan-bound-launch")
            {
                string? previousRoot = DiagnosticPaths.DataRootOverride;
                try
                {
                    DiagnosticPaths.DataRootOverride = Path.Combine(root, "unowned-controller");
                    // Use the public launch API. It deliberately marks an owned launch even without a live owner.
                    DiagnosticRuntime.BindChildStart(start);
                }
                finally { DiagnosticPaths.DataRootOverride = previousRoot; }
            }
            using var child = Process.Start(start) ?? throw new IOException("Could not start isolated exit fixture.");
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(18)); }
            catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
            string stdout = await output, stderr = await error;
            File.WriteAllText(Path.Combine(root, name + ".stdout.log"), stdout);
            File.WriteAllText(Path.Combine(root, name + ".stderr.log"), stderr);
            results.Add(new { name, exitCode = child.ExitCode, passed = child.ExitCode == 0 });
            if (child.ExitCode == 0) { passed++; Console.WriteLine("PASS " + name); }
            else Console.WriteLine("FAIL " + name + " (isolated exit code " + child.ExitCode + ")");
        }
        File.WriteAllText(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(new
        { passed, total = Cases.Length, success = passed == Cases.Length, actualHostProcesses = 0, bluetoothCalls = 0, cases = results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Exit lifecycle evidence: " + root);
        if (passed != Cases.Length) throw new InvalidOperationException($"{Cases.Length - passed} isolated exit lifecycle checks failed; see {root}");
        return passed;
    }

    private static Task RunCaseAsync(string name) => name switch
    {
        "warm-stopping-eof" => WarmStoppingEofAsync(),
        "original-pipe-stopping-ack" => OriginalPipeAckAsync(),
        "late-sticky-stopping" => LateStickyAsync(),
        "full-event-queue" => FullQueueAsync(),
        "vote-cancel-resume" => VoteCancelAsync(),
        "vote-never-reconnects" => VoteNeverReconnectsAsync(),
        "second-bridge-cannot-relaunch" => SecondBridgeAsync(),
        "atomic-cross-bridge-launch" => AtomicCrossBridgeAsync(),
        "reconnect-baseline-backpressure" => ReconnectBaselineAsync(),
        "calibration-state-backpressure" => CalibrationBackpressureAsync(),
        "orphan-bound-launch" => OrphanBoundLaunchAsync(),
        "unexpected-eof-recovers" => UnexpectedEofAsync(),
        _ => throw new ArgumentException("Unknown lifecycle case.")
    };

    private static async Task WarmStoppingEofAsync()
    {
        await using var fixture = await Harness.ConnectAsync(subscribe: false);
        var stopping = Signal<BackendEvent>();
        fixture.Bridge.EventReceived += value => { if (value.Kind == "stopping") stopping.TrySetResult(value); };
        string exitId = Guid.NewGuid().ToString("N");
        await fixture.Peer.EventAsync("stopping", exitId);
        fixture.DropPeer();
        var notification = await stopping.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require(notification.ExitRequestId == exitId, "Warm connection lost the terminal exit identity.");
        await IgnoreUnavailableAsync(() => fixture.Bridge.SubscribeAsync("main", default));
        Require(fixture.Starts == 0, "A warm connection relaunched the backend after controlled stopping and EOF.");
    }

    private static async Task OriginalPipeAckAsync()
    {
        await using var fixture = await Harness.ConnectAsync(subscribe: true);
        var stopping = Signal<BackendEvent>();
        fixture.Bridge.EventReceived += value => { if (value.Kind == "stopping") stopping.TrySetResult(value); };
        string exitId = Guid.NewGuid().ToString("N");
        await fixture.Peer.EventAsync("stopping", exitId);
        var ack = await fixture.Peer.WaitRequestAsync("stoppingAck");
        Require(ack.ExitRequestId == exitId && ack.BackendInstanceId == fixture.Peer.BackendId,
            "Stopping acknowledgement must target the original pipe, exit ID and backend generation.");
        // The fake deliberately sends no response to stoppingAck. UI delivery must not await an RPC response.
        fixture.DropPeer();
        Require((await stopping.Task.WaitAsync(TimeSpan.FromSeconds(3))).ExitRequestId == exitId,
            "Closing the acknowledged pipe discarded its already-read lifecycle event.");
        Require(fixture.Starts == 0, "Stopping acknowledgement attempted a backend restart.");
    }

    private static async Task LateStickyAsync()
    {
        await using var fixture = await Harness.ConnectAsync(subscribe: false);
        string exitId = Guid.NewGuid().ToString("N");
        await fixture.Peer.EventAsync("stopping", exitId);
        await fixture.Peer.WaitRequestAsync("stoppingAck");
        fixture.DropPeer();
        var late = Signal<BackendEvent>();
        fixture.Bridge.EventReceived += value => { if (value.Kind == "stopping") late.TrySetResult(value); };
        Require((await late.Task.WaitAsync(TimeSpan.FromSeconds(3))).ExitRequestId == exitId,
            "A UI listener attached after terminal receipt did not receive sticky stopping.");
        Require(fixture.Starts == 0, "Attaching a late UI listener caused a backend launch.");
    }

    private static async Task FullQueueAsync()
    {
        await using var fixture = await Harness.ConnectAsync(subscribe: true);
        using var release = new ManualResetEventSlim();
        var entered = Signal<bool>(); var stopping = Signal<BackendEvent>(); int first = 0;
        fixture.Bridge.EventReceived += value =>
        {
            if (value.Kind == "health" && Interlocked.Increment(ref first) == 1)
            { entered.TrySetResult(true); release.Wait(); }
            if (value.Kind == "stopping") stopping.TrySetResult(value);
        };
        try
        {
            await fixture.Peer.EventAsync("health");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (int index = 0; index < 128; index++) await fixture.Peer.EventAsync("health", message: "synthetic queued update " + index);
            string exitId = Guid.NewGuid().ToString("N");
            await fixture.Peer.EventAsync("stopping", exitId);
            await fixture.Peer.WaitRequestAsync("stoppingAck");
            Require((await stopping.Task.WaitAsync(TimeSpan.FromSeconds(3))).ExitRequestId == exitId,
                "Terminal stopping was blocked behind or discarded by the full ordinary event channel.");
            Require(!release.IsSet && fixture.Starts == 0, "The terminal channel required the blocked ordinary handler to resume.");
        }
        finally { release.Set(); }
    }

    private static async Task VoteCancelAsync()
    {
        await using var fixture = await Harness.ConnectAsync(subscribe: true);
        var request = Signal<BackendEvent>(); var cancelled = Signal<BackendEvent>();
        fixture.Bridge.EventReceived += value =>
        { if (value.Kind == "requestExit") request.TrySetResult(value); if (value.Kind == "exitCancelled") cancelled.TrySetResult(value); };
        string exitId = Guid.NewGuid().ToString("N");
        await fixture.Peer.EventAsync("requestExit", exitId);
        await request.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Bridge.AcknowledgeExitAsync(exitId, true, default).WaitAsync(TimeSpan.FromSeconds(3));
        var vote = await fixture.Peer.WaitRequestAsync("exitVote");
        Require(vote.Approved == true && vote.ExitRequestId == exitId && vote.BackendInstanceId == fixture.Peer.BackendId,
            "The exit vote was not sent on the original backend generation.");
        await fixture.Peer.EventAsync("exitCancelled", exitId);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require((await fixture.Bridge.EnumerateAsync(default)).Count == 0 && fixture.Starts == 0,
            "A cancelled exit did not restore ordinary communication on the existing pipe.");
    }

    private static async Task VoteNeverReconnectsAsync()
    {
        await using var fixture = await Harness.ConnectAsync(subscribe: true);
        var requested = Signal<bool>(); var disconnected = Signal<bool>();
        fixture.Bridge.EventReceived += value =>
        {
            if (value.Kind == "requestExit") requested.TrySetResult(true);
            if (value.Kind == "stopping") disconnected.TrySetResult(true);
        };
        string exitId = Guid.NewGuid().ToString("N");
        await fixture.Peer.EventAsync("requestExit", exitId);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.DropPeer();
        await IgnoreUnavailableAsync(() => fixture.Bridge.AcknowledgeExitAsync(exitId, true, default));
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require(fixture.Bridge.IsStopping && fixture.Starts == 0,
            "An approved vote on a retired pipe did not reach a local terminal state without launching a new backend.");
    }

    private static async Task UnexpectedEofAsync()
    {
        await using var fixture = await Harness.ConnectAsync(subscribe: true, allowRestart: true);
        fixture.DropPeer();
        var replacement = await fixture.Replacement.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var subscribe = await replacement.WaitRequestAsync("subscribe");
        Require(subscribe.Role == "main" && fixture.Starts == 1,
            "Unexpected EOF no longer performs the existing subscribed-client recovery.");
        Require((await fixture.Bridge.EnumerateAsync(default)).Count == 0, "The replacement synthetic backend is not usable.");
    }

    private static async Task SecondBridgeAsync()
    {
        await using var fixture = await Harness.ConnectAsync(subscribe: true);
        var (bridge, peer) = await fixture.AddBridgeAsync();
        var disconnected = Signal<bool>();
        bridge.EventReceived += value => { if (value.Kind is "unavailable" or "stopping") disconnected.TrySetResult(true); };
        await fixture.Peer.EventAsync("stopping", Guid.NewGuid().ToString("N"));
        await fixture.Peer.WaitRequestAsync("stoppingAck");
        fixture.DropPeer(peer);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await IgnoreUnavailableAsync(() => bridge.EnumerateAsync(default));
        Require(fixture.Starts == 0, "Another Bridge in the same process relaunched the retired backend pipe after controlled exit.");
    }

    private static async Task ReconnectBaselineAsync()
    {
        await using var fixture = await Harness.ConnectAsync(subscribe: true, allowRestart: true);
        using var release = new ManualResetEventSlim();
        var entered = Signal<bool>(); var catalog = Signal<bool>(); var snapshot = Signal<bool>(); int first = 0;
        string original = fixture.Peer.BackendId;
        fixture.Bridge.EventReceived += value =>
        {
            if (value.Kind == "health" && Interlocked.Increment(ref first) == 1) { entered.TrySetResult(true); release.Wait(); }
            if (value.BackendInstanceId != original && value.Kind == "catalog") catalog.TrySetResult(true);
            if (value.BackendInstanceId != original && value.Snapshot?.Identity == "baseline-peer") snapshot.TrySetResult(true);
        };
        try
        {
            await fixture.Peer.EventAsync("health"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (int index = 0; index < 128; index++) await fixture.Peer.EventAsync("health", message: "queued old state " + index);
            // The RPC response is behind all event records on this fake pipe: the old state queue is now full.
            await fixture.Bridge.EnumerateAsync(default);
            fixture.DropPeer();
            var replacement = await fixture.Replacement.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await replacement.WaitRequestAsync("subscribe");
            // This private field is only a scheduling barrier, not the pass/fail oracle. It avoids guessing
            // with a sleep whether the background resubscribe already tried to enqueue its baseline.
            var reconnect = typeof(ClientBridge).GetField("_reconnecting", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Missing fixture reconnect-completion synchronizer.");
            var timer = Stopwatch.StartNew();
            while ((int)reconnect.GetValue(fixture.Bridge)! != 0 && timer.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
            Require((int)reconnect.GetValue(fixture.Bridge)! == 0, "The synthetic resubscription did not complete.");
            release.Set();
            await Task.WhenAll(catalog.Task, snapshot.Task).WaitAsync(TimeSpan.FromSeconds(3));
            Require(fixture.Starts == 1, "The baseline fixture performed more than one synthetic recovery launch.");
        }
        finally { release.Set(); }
    }

    private static async Task AtomicCrossBridgeAsync()
    {
        await using var fixture = await Harness.ConnectAsync(subscribe: true);
        // An unsubscribed bridge has no automatic reconnect task competing for its connection gate.
        // It reproduces a real warm window still waiting for subscription when tray exit commits.
        var (bridge, peer) = await fixture.AddBridgeAsync(subscribe: false, testBudget: TimeSpan.FromMilliseconds(500));
        var warm = Signal<bool>(); var eof = Signal<bool>(); var stoppingA = Signal<bool>();
        bridge.EventReceived += value =>
        { if (value.Kind == "health") warm.TrySetResult(true); if (value.Kind == "unavailable") eof.TrySetResult(true); };
        fixture.Bridge.EventReceived += value => { if (value.Kind == "stopping") stoppingA.TrySetResult(true); };
        await peer.EventAsync("health"); await warm.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.DropPeer(peer); await eof.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stateGate = typeof(ClientBridge).GetField("_stateGate", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(bridge)
            ?? throw new InvalidOperationException("Missing cross-bridge fixture scheduling gate.");
        var held = Signal<bool>();
        var worker = Task.Factory.StartNew(() =>
        {
            lock (stateGate)
            {
                held.TrySetResult(true);
                stoppingA.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                Require(!bridge.IsStopping, "The fixture failed to pause propagation before B's local terminal state.");
                // Remain on this dedicated thread: the private monitor is used only for scheduling.
                // The operation under test is the normal public RPC, never a private production method.
                try { bridge.EnumerateAsync(default).GetAwaiter().GetResult(); }
                catch (IOException) { }
                catch (OperationCanceledException) { }
                Require(fixture.Starts == 0, "B invoked the launcher after A globally retired the pipe but before propagation acquired B's gate.");
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await held.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Peer.EventAsync("stopping", Guid.NewGuid().ToString("N"));
        await worker.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Peer.WaitRequestAsync("stoppingAck");
        Require(fixture.Starts == 0, "The propagation completion invoked a delayed launcher.");
    }

    private static async Task OrphanBoundLaunchAsync()
    {
        string? previousRoot = DiagnosticPaths.DataRootOverride;
        string root = Path.Combine(Path.GetTempPath(), "HeyMelody-OrphanBridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        DiagnosticPaths.DataRootOverride = root;
        try
        {
            Require(DiagnosticRuntime.HasBoundLaunch && DiagnosticRuntime.IsBoundLaunchExpired,
                "The public bound-launch fixture did not retain its invalid owner marker.");
            int starts = 0;
            await using var bridge = new ClientBridge("HeyMelody-OrphanPipe-" + Guid.NewGuid().ToString("N"),
                () => { Interlocked.Increment(ref starts); throw new IOException("No real launcher is allowed."); },
                TimeSpan.FromMilliseconds(500), () => false);
            Require(bridge.IsStopping, "A bridge bootstrapped after its bound owner disappeared did not become terminal.");
            await IgnoreUnavailableAsync(() => bridge.EnumerateAsync(default));
            var terminal = Signal<BackendEvent>();
            bridge.EventReceived += value => { if (value.Kind == "stopping") terminal.TrySetResult(value); };
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(bridge.IsStopping && starts == 0,
                "An expired owned child relaunched a backend or lost sticky stopping for its late UI listener.");
        }
        finally { DiagnosticPaths.DataRootOverride = previousRoot; }
    }

    private static async Task CalibrationBackpressureAsync()
    {
        await using var fixture = await Harness.ConnectAsync(subscribe: true);
        using var firstRelease = new ManualResetEventSlim(); using var secondRelease = new ManualResetEventSlim();
        var firstEntered = Signal<bool>(); var secondEntered = Signal<bool>();
        var latestA = Signal<bool>(); var latestB = Signal<bool>(); var stopping = Signal<bool>();
        fixture.Bridge.EventReceived += value =>
        {
            if (value.Calibration is { } state)
            {
                if (state.Handle == "A" && state.Revision == 1) { firstEntered.TrySetResult(true); firstRelease.Wait(); }
                if (state.Handle == "A" && state.Revision == 1000) latestA.TrySetResult(true);
                if (state.Handle == "B" && state.Revision == 1001) latestB.TrySetResult(true);
                if (state.Handle == "A" && state.Revision == 2000) { secondEntered.TrySetResult(true); secondRelease.Wait(); }
            }
            if (value.Kind == "stopping") stopping.TrySetResult(true);
        };
        Task State(string handle, long revision) => fixture.Peer.EventAsync("calibration",
            calibration: new UiCalibrationState { Handle = handle, Revision = revision });
        try
        {
            await State("A", 1); await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (int index = 0; index < 128; index++) await State(index % 2 == 0 ? "A" : "B", index + 2);
            await State("A", 1000); await State("B", 1001);
            await fixture.Bridge.EnumerateAsync(default);
            firstRelease.Set();
            await Task.WhenAll(latestA.Task, latestB.Task).WaitAsync(TimeSpan.FromSeconds(3));
            await State("A", 2000); await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await fixture.Peer.EventAsync("stopping", Guid.NewGuid().ToString("N"));
            await fixture.Peer.WaitRequestAsync("stoppingAck");
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(!secondRelease.IsSet && fixture.Starts == 0, "Slow calibration state delivery blocked terminal lifecycle delivery.");
        }
        finally { firstRelease.Set(); secondRelease.Set(); }
    }

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private static async Task IgnoreUnavailableAsync(Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException) { }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _pipe = "HeyMelody-ExitFixture-" + Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<NamedPipeServerStream> _listeners = [];
        private readonly ConcurrentBag<Peer> _peers = [];
        private readonly ConcurrentBag<Task> _accepts = [];
        private readonly ConcurrentBag<ClientBridge> _otherBridges = [];
        private readonly bool _allowRestart;
        private volatile bool _available = true;
        private int _starts;
        internal int Starts => Volatile.Read(ref _starts);
        internal ClientBridge Bridge { get; private set; } = null!;
        internal Peer Peer { get; private set; } = null!;
        internal TaskCompletionSource<Peer> Replacement { get; } = Signal<Peer>();
        private Harness(bool allowRestart) => _allowRestart = allowRestart;
        internal static async Task<Harness> ConnectAsync(bool subscribe, bool allowRestart = false)
        {
            var fixture = new Harness(allowRestart);
            try
            {
                var accepted = fixture.AcceptOneAsync();
                fixture.Bridge = new ClientBridge(fixture._pipe, fixture.Launch, TimeSpan.FromSeconds(3), () => fixture._available);
                Task? subscription = subscribe ? fixture.Bridge.SubscribeAsync("main", default) : null;
                if (!subscribe) fixture.Bridge.BeginConnect();
                fixture.Peer = await accepted.WaitAsync(TimeSpan.FromSeconds(3));
                if (subscription is not null) await subscription.WaitAsync(TimeSpan.FromSeconds(3));
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        internal async Task<(ClientBridge Bridge, Peer Peer)> AddBridgeAsync(bool subscribe = true, TimeSpan? testBudget = null)
        {
            var accepted = AcceptOneAsync(Peer.BackendId);
            var bridge = new ClientBridge(_pipe, Launch, testBudget ?? TimeSpan.FromSeconds(3), () => _available);
            _otherBridges.Add(bridge);
            Task? subscription = subscribe ? bridge.SubscribeAsync("main", default) : null;
            if (!subscribe) bridge.BeginConnect();
            var peer = await accepted.WaitAsync(TimeSpan.FromSeconds(3));
            if (subscription is not null) await subscription.WaitAsync(TimeSpan.FromSeconds(3));
            return (bridge, peer);
        }
        private Task<Peer> AcceptOneAsync(string? backendId = null, bool withBaseline = false)
        {
            var listener = new NamedPipeServerStream(_pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _listeners.Add(listener);
            async Task<Peer> AcceptAsync()
            {
                await listener.WaitForConnectionAsync(_stop.Token);
                var peer = new Peer(listener, backendId, withBaseline); _peers.Add(peer); return peer;
            }
            var task = AcceptAsync(); _accepts.Add(task); return task;
        }
        private void Launch()
        {
            Interlocked.Increment(ref _starts);
            if (!_allowRestart) throw new IOException("Synthetic launcher invoked; no real backend may start.");
            _available = true;
            var task = AcceptOneAsync(withBaseline: true);
            _ = task.ContinueWith(completed =>
            {
                if (completed.IsCompletedSuccessfully) Replacement.TrySetResult(completed.Result);
                else if (completed.Exception is { } error) Replacement.TrySetException(error);
            }, TaskScheduler.Default);
        }
        internal void DropPeer(Peer? peer = null) { _available = false; (peer ?? Peer).Close(); }
        public async ValueTask DisposeAsync()
        {
            if (Bridge is not null) await Bridge.DisposeAsync();
            foreach (var bridge in _otherBridges) await bridge.DisposeAsync();
            _stop.Cancel();
            foreach (var peer in _peers) await peer.DisposeAsync();
            foreach (var listener in _listeners) listener.Dispose();
            foreach (var accept in _accepts) try { await accept.WaitAsync(TimeSpan.FromSeconds(1)); } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
            _stop.Dispose();
        }
    }

    private sealed class Peer : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<BackendRequest> _requests = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<BackendRequest>> _observations = new();
        private readonly Task _readLoop;
        private readonly bool _withBaseline;
        private long _lastRequestId;
        internal string BackendId { get; } = Guid.NewGuid().ToString("N");
        internal Peer(NamedPipeServerStream pipe, string? backendId = null, bool withBaseline = false)
        {
            _withBaseline = withBaseline;
            if (backendId is not null) BackendId = backendId;
            _pipe = pipe; _reader = new(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            _writer = new(pipe, new UTF8Encoding(false), 4096, leaveOpen: true);
            _readLoop = ReadAsync();
        }
        internal Task EventAsync(string kind, string? exitId = null, string? message = null, UiCalibrationState? calibration = null) => SendAsync(new BackendMessage
        { Kind = "event", Event = new BackendEvent { Kind = kind, BackendInstanceId = BackendId, ExitRequestId = exitId, Message = message, Calibration = calibration } });
        internal async Task<BackendRequest> WaitRequestAsync(string operation)
        {
            var observed = _observations.GetOrAdd(operation, _ => Signal<BackendRequest>());
            var existing = _requests.FirstOrDefault(request => request.Operation == operation);
            if (existing is not null) observed.TrySetResult(existing);
            return await observed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        private async Task ReadAsync()
        {
            try
            {
                while (await _reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    var request = JsonSerializer.Deserialize<BackendRequest>(line, RpcWire.Json) ?? throw new InvalidDataException("Null fake request.");
                    if (request.Id <= _lastRequestId) throw new InvalidOperationException("Fake backend requires a positive, strictly increasing request ID, including stoppingAck.");
                    _lastRequestId = request.Id;
                    _requests.Enqueue(request);
                    _observations.GetOrAdd(request.Operation, _ => Signal<BackendRequest>()).TrySetResult(request);
                    if (request.Operation == "stoppingAck") continue;
                    object data = request.Operation switch
                    {
                        "subscribe" => new BackendSubscription { BackendInstanceId = BackendId,
                            Devices = _withBaseline ? [new("baseline-peer", "Synthetic baseline", true)] : [],
                            Snapshots = _withBaseline ? [new UiSnapshot { Identity = "baseline-peer", BackendInstanceId = BackendId,
                                Connected = true, SupportedModel = true, ConnectionState = ConnectionState.Connected }] : [] },
                        "enumerate" => new List<UiDevice>(),
                        _ => true
                    };
                    await SendAsync(new BackendMessage { Id = request.Id, Success = true, Data = JsonSerializer.SerializeToElement(data, RpcWire.Json) });
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
        }
        private async Task SendAsync(BackendMessage message)
        {
            await _write.WaitAsync(_stop.Token);
            try { await _writer.WriteLineAsync(JsonSerializer.Serialize(message, RpcWire.Json)); await _writer.FlushAsync(_stop.Token); }
            finally { _write.Release(); }
        }
        internal void Close() { _stop.Cancel(); _pipe.Dispose(); }
        public async ValueTask DisposeAsync()
        {
            Close(); await _readLoop.WaitAsync(TimeSpan.FromSeconds(1));
            try { _writer.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
            _reader.Dispose(); _stop.Dispose(); _write.Dispose();
        }
    }
}
