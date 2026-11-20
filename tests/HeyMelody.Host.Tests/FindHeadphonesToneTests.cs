using System.Collections.Concurrent;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;

internal static partial class FindHeadphonesTests
{
    private static async Task RunToneAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        await test("find tone schema 5 pins target and requires main role, consent and current wear evidence", async () =>
        {
            var worker = new ToneWorker();
            await using var fixture = await Fixture.Create(scratch, workerFactory: () => worker);
            await Until(() => fixture.Backend.Subscribe().Snapshots.Single().FindHeadphonesTone?.CanStart == true);
            var pipe = "HeyMelody.ToneTests." + Guid.NewGuid().ToString("N");
            await using var server = new BackendPipeServer(fixture.Backend, pipe);
            server.Start();
            await using var main = new ClientBridge(pipe, testBudget: TimeSpan.FromSeconds(5));
            await main.SubscribeAsync("main", default);
            var ready = await main.SelectAsync(fixture.Identity, default);
            var target = new FindHeadphonesToneTarget(ready.BackendInstanceId, ready.Identity, ready.DeviceGeneration, ready.FindHeadphonesTone!.Session);
            await using var observer = new ClientBridge(pipe, testBudget: TimeSpan.FromSeconds(5));
            await observer.SubscribeAsync("observer", default);
            Check(!(await observer.SetFindHeadphonesToneAsync(target, true, true, false, default)).Submitted);
            Check(!(await main.SetFindHeadphonesToneAsync(target with { BackendInstanceId = "retired" }, true, true, false, default)).Submitted);
            Check(!(await main.SetFindHeadphonesToneAsync(target with { DeviceGeneration = target.DeviceGeneration + 1 }, true, true, false, default)).Submitted);
            Check(!(await main.SetFindHeadphonesToneAsync(target with { Session = target.Session + 1 }, true, true, false, default)).Submitted);
            Check(!(await main.SetFindHeadphonesToneAsync(target, true, false, false, default)).Submitted);
            worker.Worn();
            Check(!(await main.SetFindHeadphonesToneAsync(target, true, true, false, default)).Submitted && worker.Operations.IsEmpty);
            var started = await main.SetFindHeadphonesToneAsync(target, true, true, true, default);
            Check(started.AckStatus == 0 && started.After?.FindHeadphonesTone?.Phase == FindHeadphonesTonePhase.Ringing
                && worker.Operations.TryPeek(out var submission) && submission is { Play: true, Confirmed: true, ConfirmedWorn: true });
            Check(!(await main.SetFindHeadphonesToneAsync(target, true, true, true, default)).Submitted);
            Check(!(await main.ApplyAsync(target.Identity, target.DeviceGeneration, new("GameMode", 1), default)).SubmissionAttempted);
            Check(!(await fixture.Backend.GetFindHeadphonesAsync(default)).Enabled && fixture.Location.Calls == 0);
            await main.DisposeAsync();
            await Until(() => worker.Operations.Count == 2);
            Check(worker.Operations.Select(item => item.Play).SequenceEqual(new bool?[] { true, false }));
        });

        await test("find tone disconnect clears active controls and refuses old stop without replay", async () =>
        {
            var worker = new ToneWorker();
            await using var fixture = await Fixture.Create(scratch, workerFactory: () => worker);
            await Until(() => fixture.Backend.Subscribe().Snapshots.Single().FindHeadphonesTone?.CanStart == true);
            var ready = fixture.Backend.Select(fixture.Identity);
            var session = ready.FindHeadphonesTone!.Session;
            var started = await fixture.Backend.SetFindHeadphonesToneAsync("main", ready.Identity, ready.DeviceGeneration,
                new(session, true, true), default);
            Check(started.AckStatus == 0);
            fixture.Backend.ObserveConnection(1, false);
            var disconnected = fixture.Backend.Select(ready.Identity);
            Check(disconnected.FindHeadphonesTone is { CanStart: false, Phase: FindHeadphonesTonePhase.Unconfirmed }
                && disconnected.DeviceGeneration > ready.DeviceGeneration);
            var stop = await fixture.Backend.SetFindHeadphonesToneAsync("main", ready.Identity, ready.DeviceGeneration,
                new(session, false), default);
            Check(!stop.Submitted && stop.Message.Contains("未确认") && worker.Operations.Count == 1);
        });
    }

    private sealed class ToneWorker : IPeerWorker
    {
        private UiSnapshot _snapshot = new();
        internal ConcurrentQueue<FindHeadphonesToneArgs> Operations { get; } = new();
        public event Action<UiSnapshot>? SnapshotChanged;
        public event Action? Unavailable { add { } remove { } }

        public Task<UiSnapshot> InitializeAsync(ulong address, string identity, string name, CancellationToken token) =>
            Task.FromResult(_snapshot = new()
            {
                Identity = identity, DeviceName = name, Connected = true, ControlReady = true, SupportedModel = true,
                ConnectionState = ConnectionState.Connected, ProductId = "06EC10", Revision = 1,
                FindHeadphonesTone = new(1, FindHeadphonesTonePhase.Idle, true, false)
            });

        public Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken token) => Task.FromResult(_snapshot);
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw new InvalidOperationException("Unexpected settings write.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<UiFindHeadphonesToneResult> SetFindHeadphonesToneAsync(string identity, long generation, FindHeadphonesToneArgs args,
            CancellationToken token)
        {
            Check(_snapshot.Identity == identity && args.Session == _snapshot.FindHeadphonesTone?.Session);
            var next = _snapshot.Clone();
            next.Revision++;
            next.FindHeadphonesTone = next.FindHeadphonesTone! with
            {
                Phase = args.Play == true ? FindHeadphonesTonePhase.Ringing : FindHeadphonesTonePhase.Idle,
                CanStart = args.Play == false
            };
            _snapshot = next;
            Operations.Enqueue(args);
            return Task.FromResult(new UiFindHeadphonesToneResult(true, 0, "", next));
        }

        internal void Worn()
        {
            var next = _snapshot.Clone();
            next.Revision++;
            next.FindHeadphonesTone = next.FindHeadphonesTone! with { Worn = true };
            _snapshot = next;
            SnapshotChanged?.Invoke(next);
        }
    }
}
