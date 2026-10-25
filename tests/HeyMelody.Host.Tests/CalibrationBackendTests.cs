using HeyMelody.Platform;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;

internal static class CalibrationBackendTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        await test("spectrum telemetry is owner-targeted, epoch-qualified and never invalidates action revisions", async () =>
        {
            await using var fixture = await Fixture.Create(scratch);
            var snapshot = fixture.Snapshots()[0]; var worker = fixture.Workers[0];
            var ready = await fixture.Backend.CreateCalibrationAsync("launcher", snapshot.Identity, snapshot.DeviceGeneration, "hearing", default);
            fixture.Backend.GetCalibration("component", ready.Handle, "calibration");
            var playing = ready with { Revision = ready.Revision + 1, Stage = "Previewing", PreviewMode = "Enhanced",
                PreviewAudioPlaying = true, PreviewEpoch = 3 };
            worker.EmitCalibration(playing);
            long controlRevision = fixture.Backend.GetCalibration("component", ready.Handle, null).Revision;
            long peerRevision = fixture.Backend.Select(snapshot.Identity).Revision;
            var received = new List<(string Owner, BackendEvent Event)>();
            fixture.Backend.CalibrationOwnerEventPublished += (owner, notification) => received.Add((owner, notification));
            var frame = new UiCalibrationSpectrum(ready.Handle, "", snapshot.DeviceGeneration, 3, 1, [.2,.3,.4,.5,.6,.7]);
            for (int i = 1; i <= 250; i++) worker.EmitSpectrum(frame with { Sequence = i });
            Check(received.Count == 250 && received.All(x => x.Owner == "component" && x.Event.CalibrationSpectrum?.BackendInstanceId == fixture.Backend.InstanceId),
                "Frames were broadcast to observers/launcher or lacked backend qualification");
            Check(fixture.Backend.GetCalibration("component", ready.Handle, null).Revision == controlRevision
                && fixture.Backend.Select(snapshot.Identity).Revision == peerRevision, "Spectrum invalidated a control or device revision");
            worker.EmitSpectrum(frame with { Sequence = 200 });
            worker.EmitSpectrum(frame with { Sequence = 300, PreviewEpoch = 2 });
            worker.EmitSpectrum(frame with { Sequence = 300, Generation = snapshot.DeviceGeneration + 1 });
            worker.EmitSpectrum(frame with { Sequence = 300, Handle = new string('a',32) });
            worker.EmitSpectrum(frame with { Sequence = 300, Levels = [double.NaN,.2,.2,.2,.2,.2] });
            Check(received.Count == 250, "Stale or invalid spectra escaped lease qualification");
            playing = playing with { Revision = controlRevision + 1, PreviewMode = "Original", PreviewEpoch = 4 };
            worker.EmitCalibration(playing);
            controlRevision = fixture.Backend.GetCalibration("component", ready.Handle, null).Revision;
            worker.EmitSpectrum(frame with { Sequence = 300 });
            Check(received.Count == 250, "Enhanced telemetry reached a newer original audition epoch");
            frame = frame with { PreviewEpoch = 4, Sequence = 1 };
            worker.EmitSpectrum(frame);
            Check(received.Count == 251 && received[^1].Event.CalibrationSpectrum?.PreviewEpoch == 4
                && fixture.Backend.GetCalibration("component", ready.Handle, null).Revision == controlRevision,
                "Original telemetry was rejected or advanced its action revision");
            worker.EmitSpectrum(frame);
            Check(received.Count == 251, "Original audition accepted a duplicate sequence");
            await fixture.Backend.AdvanceCalibrationAsync("component", ready.Handle, controlRevision, new("preview-stop"), default);
            worker.EmitSpectrum(frame with { Sequence = 400 });
            Check(received.Count == 251 && worker.Advances == 1, "Telemetry raced a valid action or revived after preview ended");
            worker.EmitCalibration(playing with { Revision = worker.State.Revision + 1, PreviewEpoch = 4 });
            fixture.Backend.Pause(snapshot.Identity);
            var invalidated = fixture.Backend.GetCalibration("component", ready.Handle, null);
            Check(invalidated.Stage == "Cancelled" && !invalidated.PreviewAudioPlaying && invalidated.PreviewMode == "Stopped"
                && invalidated.PreviewEpoch > 4, "Connection invalidation retained live playback authority or its epoch");
            worker.EmitSpectrum(frame with { Sequence = 500 });
            Check(received.Count == 251, "Previous worker delivered after a generation change");
        });
        await test("a blocked pipe coalesces music spectra without consuming reliable reply capacity", async () =>
        {
            await using var fixture = await Fixture.Create(scratch);
            await using var server = new BackendPipeServer(fixture.Backend, "HeyMelody-outbox-" + Guid.NewGuid().ToString("N"));
            using var pipe = new NamedPipeServerStream("HeyMelody-blocked-" + Guid.NewGuid().ToString("N"));
            var type = typeof(BackendPipeServer).GetNestedType("Client", BindingFlags.NonPublic)!;
            using var client = (IDisposable)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [pipe, server], null)!;
            var send = type.GetMethod("SendEvent")!;
            var frame = new UiCalibrationSpectrum(new string('a',32), fixture.Backend.InstanceId, 1, 1, 1, [.1,.2,.3,.4,.5,.6]);
            for (int i = 1; i <= 10000; i++) send.Invoke(client, [new BackendEvent { Kind = "calibrationSpectrum", CalibrationSpectrum = frame with { Sequence = i } }]);
            type.GetMethod("Reply")!.Invoke(client, [7L, true, null, null]);
            var updates = (Dictionary<string, BackendPipeServer.Outbound>)type.GetField("_updates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
            int replies = (int)type.GetField("_criticalCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
            var lifetime = (CancellationTokenSource)type.GetProperty("Lifetime")!.GetValue(client)!;
            Check(updates.Count == 1 && updates.Values.Single().Message.Event?.CalibrationSpectrum?.Sequence == 10000
                && replies == 1 && !lifetime.IsCancellationRequested, "Slow-reader spectra grew an unbounded queue or displaced reliable replies");
        });
        await test("unconfirmed calibration STOP stays failed and leased until an acknowledged retry", async () =>
        {
            foreach (bool throwFailure in new[] { false, true })
            {
                await using var fixture = await Fixture.Create(scratch);
                var snapshot = fixture.Snapshots()[0]; var worker = fixture.Workers[0];
                var ready = await fixture.Backend.CreateCalibrationAsync("editor", snapshot.Identity, snapshot.DeviceGeneration, "fit", default);
                worker.FailCancel = true; worker.ThrowCancel = throwFailure;
                var failed = await fixture.Backend.CancelCalibrationAsync("editor", ready.Handle);
                Check(failed.Stage == "Failed" && !fixture.Backend.Select(snapshot.Identity).ControlReady, "Unconfirmed STOP released the lease or claimed cancellation");
                await Throws<InvalidOperationException>(() => fixture.Backend.CreateCalibrationAsync("other", snapshot.Identity, snapshot.DeviceGeneration, "fit", default));
                worker.FailCancel = false;
                var stopped = await fixture.Backend.CancelCalibrationAsync("editor", ready.Handle);
                Check(stopped.Stage == "Cancelled" && stopped.Revision > failed.Revision && fixture.Backend.Select(snapshot.Identity).ControlReady,
                    "Acknowledged STOP retry failed to release the lease");
            }
        });
        await test("late confirmed cancellation releases a lease after an IPC failure", async () =>
        {
            await using var fixture = await Fixture.Create(scratch);
            var snapshot = fixture.Snapshots()[0]; var worker = fixture.Workers[0];
            var ready = await fixture.Backend.CreateCalibrationAsync("editor", snapshot.Identity, snapshot.DeviceGeneration, "fit", default);
            worker.FailCancel = worker.ThrowCancel = true;
            var failed = await fixture.Backend.CancelCalibrationAsync("editor", ready.Handle);
            Check(!fixture.Backend.Select(snapshot.Identity).ControlReady, "Failed stop released lease");
            worker.EmitCalibration(ready with { Stage = "Cancelled", Revision = failed.Revision + 1 });
            Check(fixture.Backend.GetCalibration("editor", ready.Handle, null).Stage == "Cancelled"
                && fixture.Backend.Select(snapshot.Identity).ControlReady, "Late confirmed stop kept the device blocked");
        });
        await test("calibration lease excludes ordinary SET on its device and preserves other devices", async () =>
        {
            await using var fixture = await Fixture.Create(scratch, 2);
            var first = fixture.Snapshots()[0]; var second = fixture.Snapshots()[1];
            var state = await fixture.Backend.CreateCalibrationAsync("launcher", first.Identity, first.DeviceGeneration, "fit", default);
            Check(!fixture.Backend.Select(first.Identity).ControlReady, "Leased device still accepts ordinary writes");
            Check(fixture.Backend.Select(second.Identity).ControlReady, "Lease disabled another device");
            var blocked = await fixture.Backend.ApplyAsync(first.Identity, first.DeviceGeneration, new("GameMode", 1), default);
            Check(!blocked.TargetSubmitted && blocked.FailureKind == ApplyFailure.StateUnavailable && fixture.Workers[0].Applies == 0, "Ordinary SET escaped the lease");
            await Throws<InvalidOperationException>(() => fixture.Backend.SelectHearingAsync(first.Identity, first.DeviceGeneration, null, default));
            await Throws<InvalidOperationException>(() => fixture.Backend.CreateCalibrationAsync("another", first.Identity, first.DeviceGeneration, "hearing", default));
            var other = await fixture.Backend.ApplyAsync(second.Identity, second.DeviceGeneration, new("GameMode", 1), default);
            Check(other.TargetSubmitted && fixture.Workers[1].Applies == 1, "Other device write was blocked");
            var cancelled = await fixture.Backend.CancelCalibrationAsync("launcher", state.Handle);
            Check(cancelled.Stage == "Cancelled" && fixture.Backend.Select(first.Identity).ControlReady, "Cancel did not release lease");
            Check((await fixture.Backend.ApplyAsync(first.Identity, first.DeviceGeneration, new("GameMode", 1), default)).TargetSubmitted, "Device remained blocked after cancel");
        });
        await test("measurement waiting releases command gate and independent events complete the lease", async () =>
        {
            await using var fixture = await Fixture.Create(scratch);
            var snapshot = fixture.Snapshots()[0]; var worker = fixture.Workers[0];
            var ready = await fixture.Backend.CreateCalibrationAsync("editor", snapshot.Identity, snapshot.DeviceGeneration, "fit", default);
            var running = await fixture.Backend.AdvanceCalibrationAsync("editor", ready.Handle, ready.Revision, new("start"), default).WaitAsync(TimeSpan.FromSeconds(2));
            Check(running.Stage == "FitTesting", "Start waited for measurement completion");
            var read = await fixture.Backend.RefreshAsync(snapshot.Identity, RefreshScope.Full, default).WaitAsync(TimeSpan.FromSeconds(2));
            Check(!read.ControlReady && worker.Refreshes == 0, "Read during measurement issued conflicting GET or held command gate");
            worker.EmitCalibration(running with { Stage = "Completed", Revision = running.Revision + 1, FitResults = [new(1, 1), new(2, 6)] });
            Check(fixture.Backend.GetCalibration("editor", ready.Handle, null).FitResults.Count == 2, "Measurement event was not adopted");
            Check(fixture.Backend.Select(snapshot.Identity).ControlReady, "Completion did not release the lease");
            Check((await fixture.Backend.ApplyAsync(snapshot.Identity, snapshot.DeviceGeneration, new("GameMode", 1), default).WaitAsync(TimeSpan.FromSeconds(2))).TargetSubmitted, "Command gate held after result event");
        });
        await test("calibration stale revision and previous connection generation cannot submit", async () =>
        {
            await using var fixture = await Fixture.Create(scratch);
            var snapshot = fixture.Snapshots()[0]; var worker = fixture.Workers[0];
            var ready = await fixture.Backend.CreateCalibrationAsync("editor", snapshot.Identity, snapshot.DeviceGeneration, "hearing", default);
            var current = await fixture.Backend.AdvanceCalibrationAsync("editor", ready.Handle, ready.Revision, new("start"), default);
            int submits = worker.Advances;
            await Throws<InvalidOperationException>(() => fixture.Backend.AdvanceCalibrationAsync("editor", ready.Handle, ready.Revision, new("next"), default));
            Check(worker.Advances == submits, "Duplicate click reached worker");
            worker.EmitCalibration(ready with { Stage = "Completed" });
            Check(fixture.Backend.GetCalibration("editor", ready.Handle, null).Revision == current.Revision, "Older event replaced current progress");
            fixture.Backend.Pause(snapshot.Identity);
            Check(fixture.Backend.GetCalibration("editor", ready.Handle, null).Stage == "Cancelled", "Connection change did not invalidate operation");
            fixture.Backend.Resume(snapshot.Identity);
            await Until(() => fixture.Backend.Select(snapshot.Identity).ControlReady);
            var generation = fixture.Backend.Select(snapshot.Identity).DeviceGeneration;
            Check(generation != snapshot.DeviceGeneration, "Connection generation did not change");
            await Throws<InvalidOperationException>(() => fixture.Backend.AdvanceCalibrationAsync("editor", ready.Handle, current.Revision, new("next"), default));
            await Throws<InvalidOperationException>(() => fixture.Backend.CreateCalibrationAsync("editor", snapshot.Identity, snapshot.DeviceGeneration, "fit", default));
            worker.EmitCalibration(current with { Stage = "Completed", Revision = current.Revision + 100 });
            Check(fixture.Backend.GetCalibration("editor", ready.Handle, null).Stage == "Cancelled", "Previous worker event revived cancelled operation");
        });
        await test("component claims handle and creator EOF cannot cancel the new owner's lease", async () =>
        {
            await using var fixture = await Fixture.Create(scratch);
            var snapshot = fixture.Snapshots()[0]; var worker = fixture.Workers[0];
            var ready = await fixture.Backend.CreateCalibrationAsync("launcher", snapshot.Identity, snapshot.DeviceGeneration, "fit", default);
            await Throws<InvalidOperationException>(() => Task.FromResult(fixture.Backend.GetCalibration("observer", ready.Handle, "observer")));
            fixture.Backend.GetCalibration("component", ready.Handle, "calibration"); fixture.Backend.ClientDetached("launcher");
            await Throws<InvalidOperationException>(() => Task.FromResult(fixture.Backend.GetCalibration("second-component", ready.Handle, "calibration")));
            Check(worker.Cancels == 0 && !fixture.Backend.Select(snapshot.Identity).ControlReady, "Creator EOF stopped component-owned operation");
            await Throws<InvalidOperationException>(() => fixture.Backend.CancelCalibrationAsync("launcher", ready.Handle));
            fixture.Backend.ClientDetached("component"); await Until(() => worker.Cancels == 1);
            Check(fixture.Backend.Select(snapshot.Identity).ControlReady, "Component EOF did not release lease");
        });
        await test("calibration pipe EOF stops operation and schema 3 is rejected without losing schema 4 connection", async () =>
        {
            await using var fixture = await Fixture.Create(scratch);
            var snapshot = fixture.Snapshots()[0]; var ready = await fixture.Backend.CreateCalibrationAsync("launcher", snapshot.Identity, snapshot.DeviceGeneration, "fit", default);
            string name = "HeyMelody-calibration-eof-" + Guid.NewGuid().ToString("N");
            await using var server = new BackendPipeServer(fixture.Backend, name); server.Start();
            await using (var client = await PipeClient.Connect(name))
            {
                var baseline = await client.Request(new() { Id = 1, Operation = "subscribe", Role = "calibration" });
                Check(baseline.Success && baseline.SchemaVersion == 4, "Calibration component did not subscribe using schema 4");
                var mismatch = await client.Request(new() { SchemaVersion = 3, Id = 2, Operation = "enumerate" });
                Check(mismatch.ErrorCode == "versionMismatch", "Old schema silently accepted");
                var claimed = await client.Request(new() { Id = 3, Operation = "getCalibration", CalibrationHandle = ready.Handle });
                Check(claimed.Success, "Valid schema 4 operation failed after version rejection");
            }
            await Until(() => fixture.Workers[0].Cancels == 1 && fixture.Backend.Select(snapshot.Identity).ControlReady);
            Check(fixture.Workers[0].State.Stage == "Cancelled", "EOF did not stop the owned operation");
            await Throws<InvalidOperationException>(() => Task.FromResult(fixture.Backend.GetCalibration("launcher", ready.Handle, "calibration")));
            await Throws<InvalidOperationException>(() => Task.FromResult(fixture.Backend.GetCalibration("second-component", ready.Handle, "calibration")));
        });
        await test("pipe treats a null uid or color that the wire omitted as null instead of failing", async () =>
        {
            await using var fixture = await Fixture.Create(scratch);
            var snapshot = fixture.Snapshots()[0];
            string name = "HeyMelody-null-args-" + Guid.NewGuid().ToString("N");
            await using var server = new BackendPipeServer(fixture.Backend, name); server.Start();
            await using var client = await PipeClient.Connect(name);
            Check((await client.Request(new() { Id = 1, Operation = "subscribe", Role = "main" })).Success, "Main role unavailable");
            var off = JsonSerializer.SerializeToElement(new HearingSelectionArgs(null), RpcWire.Json);
            Check(!off.TryGetProperty("uid", out _), "The wire no longer omits a null uid; this test would not cover the omission");
            var hearing = await client.Request(new() { Id = 2, Operation = "selectHearing", Identity = snapshot.Identity, BackendInstanceId = fixture.Backend.InstanceId,
                DeviceGeneration = snapshot.DeviceGeneration, Action = off });
            Check(hearing.Success && fixture.Workers[0].HearingSelections.SequenceEqual([null]), "Turning hearing enhancement off failed or sent a uid");
            var explicitNull = await client.Request(new() { Id = 3, Operation = "selectHearing", Identity = snapshot.Identity, BackendInstanceId = fixture.Backend.InstanceId,
                DeviceGeneration = snapshot.DeviceGeneration, Action = JsonDocument.Parse("{\"uid\":null}").RootElement.Clone() });
            var chosen = await client.Request(new() { Id = 4, Operation = "selectHearing", Identity = snapshot.Identity, BackendInstanceId = fixture.Backend.InstanceId,
                DeviceGeneration = snapshot.DeviceGeneration, Action = JsonSerializer.SerializeToElement(new HearingSelectionArgs(7), RpcWire.Json) });
            Check(explicitNull.Success && chosen.Success && fixture.Workers[0].HearingSelections.SequenceEqual([null, null, 7]), "An explicit null or a uid was misread");
            var artwork = await client.Request(new() { Id = 5, Operation = "acquireArtwork",
                Action = JsonSerializer.SerializeToElement(new ArtworkAcquireArgs("06EC10", null, "hero"), RpcWire.Json) });
            Check(artwork.Success, "Artwork without a colour hint failed");
            var missingRole = await client.Request(new() { Id = 6, Operation = "acquireArtwork", Action = JsonDocument.Parse("{\"productId\":\"06EC10\"}").RootElement.Clone() });
            var noAction = await client.Request(new() { Id = 7, Operation = "releaseArtwork" });
            var textColor = await client.Request(new() { Id = 8, Operation = "acquireArtwork",
                Action = JsonDocument.Parse("{\"productId\":\"06EC10\",\"color\":\"3\",\"role\":\"hero\"}").RootElement.Clone() });
            Check(missingRole.ErrorCode == "operationFailed" && noAction.ErrorCode == "operationFailed" && textColor.ErrorCode == "operationFailed"
                && missingRole.Error == noAction.Error && textColor.Error == noAction.Error && !missingRole.Error!.Contains("Object", StringComparison.Ordinal),
                "A missing or mistyped argument was accepted or leaked a runtime message");
        });
        await test("independent calibration window can veto Host exit and approval stops its operation", async () =>
        {
            await using var fixture = await Fixture.Create(scratch);
            var snapshot = fixture.Snapshots()[0]; var ready = await fixture.Backend.CreateCalibrationAsync("launcher", snapshot.Identity, snapshot.DeviceGeneration, "fit", default);
            string name = "HeyMelody-calibration-exit-" + Guid.NewGuid().ToString("N");
            await using var server = new BackendPipeServer(fixture.Backend, name); server.Start(); bool approved = false;
            server.ExitApproved += () => { approved = true; return Task.CompletedTask; };
            await using var client = await PipeClient.Connect(name);
            Check((await client.Request(new() { Id = 1, Operation = "subscribe", Role = "calibration" })).Success, "Component role unavailable");
            Check((await client.Request(new() { Id = 2, Operation = "getCalibration", CalibrationHandle = ready.Handle })).Success, "Component could not claim operation");
            var exit = server.RequestExitAsync(); var vote = await client.ReadUntil(message => message.Event?.Kind == "requestExit");
            Check((await client.Request(new() { Id = 3, Operation = "exitVote", BackendInstanceId = fixture.Backend.InstanceId, ExitRequestId = vote.Event!.ExitRequestId, Approved = false })).Success, "Component veto rejected");
            await client.ReadUntil(message => message.Event?.Kind == "exitCancelled"); await exit.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!approved && fixture.Workers[0].Cancels == 0, "Veto shut down Host or stopped measurement");
            exit = server.RequestExitAsync(); vote = await client.ReadUntil(message => message.Event?.Kind == "requestExit");
            Check((await client.Request(new() { Id = 4, Operation = "exitVote", BackendInstanceId = fixture.Backend.InstanceId, ExitRequestId = vote.Event!.ExitRequestId, Approved = true })).Success, "Component approval rejected");
            var stopping = await client.ReadUntil(message => message.Event?.Kind == "stopping");
            await client.Send(new() { Id = 5, Operation = "stoppingAck", BackendInstanceId = fixture.Backend.InstanceId, ExitRequestId = stopping.Event!.ExitRequestId });
            await exit.WaitAsync(TimeSpan.FromSeconds(3));
            Check(approved && fixture.Workers[0].Cancels == 1, "Approved exit did not stop component operation");
        });
    }

    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Until(Func<bool> predicate)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4)); while (!predicate()) await Task.Delay(10, timeout.Token); }

    private sealed class Fixture : IAsyncDisposable
    {
        internal BackendSupervisor Backend { get; }
        internal List<CalibrationPeer> Workers { get; } = [];
        private Fixture(string path, int count)
        {
            var devices = Enumerable.Range(1, count).Select(index => new BluetoothCandidate((ulong)index, "耳机 " + index, true, true, true, 0x404)).ToArray();
            var store = new DeviceWhitelistStore(path); store.Save(devices.Select(device => DeviceWhitelistStore.KeyFor(device.Address)));
            Backend = new(store, _ => Task.FromResult<IReadOnlyList<BluetoothCandidate>>(devices), () => { var worker = new CalibrationPeer(); Workers.Add(worker); return worker; });
        }
        internal static async Task<Fixture> Create(string scratch, int count = 1)
        {
            var fixture = new Fixture(Path.Combine(scratch, "calibration-" + Guid.NewGuid().ToString("N") + ".json"), count);
            await fixture.Backend.InitializeAsync(default); await Until(() => fixture.Snapshots().Count == count && fixture.Snapshots().All(snapshot => snapshot.ControlReady)); return fixture;
        }
        internal IReadOnlyList<UiSnapshot> Snapshots() => Backend.Subscribe().Snapshots;
        public ValueTask DisposeAsync() => Backend.DisposeAsync();
    }

    private sealed class CalibrationPeer : IPeerWorker, ICalibrationPeerWorker
    {
        public event Action<UiSnapshot>? SnapshotChanged;
        public event Action? Unavailable;
        public event Action<UiCalibrationState>? CalibrationChanged;
        public event Action<UiCalibrationSpectrum>? CalibrationSpectrumChanged;
        private UiSnapshot _snapshot = new();
        private UiCalibrationState _state = new();
        internal UiCalibrationState State => _state;
        internal int Applies, Advances, Cancels, Refreshes;
        internal bool FailCancel, ThrowCancel;
        public Task<UiSnapshot> InitializeAsync(ulong address, string identity, string name, CancellationToken token)
        {
            _snapshot = new() { Identity = identity, DeviceName = name, ProductId = "06EC10", Revision = 1, Connected = true,
                ConnectionState = ConnectionState.Connected, ControlReady = true, SupportedModel = true, CanSetGameMode = true,
                CanFitTest = true, CanSetupHearing = true, LeftPlacement = EarbudPlacement.InEar, RightPlacement = EarbudPlacement.InEar };
            SnapshotChanged?.Invoke(_snapshot); return Task.FromResult(_snapshot);
        }
        public Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken token) { Refreshes++; return Task.FromResult(_snapshot); }
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        {
            Applies++; _snapshot = JsonSerializer.Deserialize<UiSnapshot>(JsonSerializer.Serialize(_snapshot, RpcWire.Json), RpcWire.Json)!;
            _snapshot.Revision++; _snapshot.GameMode = action.Value == 1; SnapshotChanged?.Invoke(_snapshot);
            return Task.FromResult(new UiApplyResult { BackendReceiptReceived = true, TargetSubmitted = true, SubmissionAttempted = true, Accepted = true, ReadbackMatches = true, After = _snapshot });
        }
        public Task<UiCalibrationState> CreateCalibrationAsync(string handle, string kind, CancellationToken token)
        { _state = new() { Handle = handle, Workflow = kind, Stage = "Ready", Revision = 1 }; return Task.FromResult(_state); }
        public Task<UiCalibrationState> GetCalibrationAsync(string handle, CancellationToken token) => Task.FromResult(_state);
        public Task<UiCalibrationState> AdvanceCalibrationAsync(string handle, long revision, UiCalibrationInput input, CancellationToken token)
        {
            Advances++; _state = _state with { Revision = _state.Revision + 1, Stage = _state.Workflow == "fit" ? "FitTesting" : "Measuring" };
            return Task.FromResult(_state); // Measurement results arrive later through CalibrationChanged.
        }
        internal void EmitCalibration(UiCalibrationState state) { _state = state; CalibrationChanged?.Invoke(state); }
        internal void EmitSpectrum(UiCalibrationSpectrum spectrum) => CalibrationSpectrumChanged?.Invoke(spectrum);
        public Task<UiCalibrationState> CancelCalibrationAsync(string handle, CancellationToken token)
        {
            Cancels++;
            if (FailCancel && ThrowCancel) throw new IOException("STOP response unavailable.");
            _state = _state with { Revision = _state.Revision + 1, Stage = FailCancel ? "Failed" : "Cancelled" };
            CalibrationChanged?.Invoke(_state); return Task.FromResult(_state);
        }
        public Task<IReadOnlyList<UiHearingProfile>> HearingProfilesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<UiHearingProfile>>([]);
        public Task<UiApplyResult> SelectHearingAsync(int? uid, CancellationToken token)
        { HearingSelections.Add(uid); return Task.FromResult(new UiApplyResult { TargetSubmitted = true }); }
        internal readonly List<int?> HearingSelections = [];
        public ValueTask DisposeAsync() { Unavailable?.Invoke(); return ValueTask.CompletedTask; }
    }

    private sealed class PipeClient(NamedPipeClientStream pipe) : IAsyncDisposable
    {
        private readonly StreamWriter _writer = new(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        private RpcLineReader? _lines;
        internal static async Task<PipeClient> Connect(string name)
        { var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(3000); return new(pipe); }
        internal async Task<BackendMessage> Request(BackendRequest request)
        { await Send(request); return await ReadUntil(message => message.Kind == "response" && message.Id == request.Id); }
        internal Task Send(BackendRequest request) => _writer.WriteLineAsync(JsonSerializer.Serialize(request, RpcWire.Json));
        internal async Task<BackendMessage> ReadUntil(Func<BackendMessage, bool> match)
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                string line = await (_lines ??= new(pipe)).ReadLineAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) ?? throw new IOException("Pipe closed");
                var message = JsonSerializer.Deserialize<BackendMessage>(line, RpcWire.Json)!; if (match(message)) return message;
            }
            throw new InvalidOperationException("Expected pipe event not found");
        }
        public async ValueTask DisposeAsync() { await _writer.DisposeAsync(); await pipe.DisposeAsync(); }
    }
}
