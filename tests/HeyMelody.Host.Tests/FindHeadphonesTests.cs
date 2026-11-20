using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HeyMelody.Platform;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;

internal static partial class FindHeadphonesTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        await RunToneAsync(test, scratch);
        await test("find headphones defaults and permission preparation never enable recording", async () =>
        {
            await using var fixture = await Fixture.Create(scratch);
            var state = await fixture.Backend.GetFindHeadphonesAsync(default);
            Check(!state.Enabled && !state.Devices.Single().Enabled && state.Devices.Single().Location is null);
            fixture.Backend.ObserveConnection(1, false);
            Check(fixture.Location.Calls == 0 && fixture.Location.Requests == 0);
            await fixture.Backend.RequestLocationPermissionAsync(default);
            Check(!(await fixture.Backend.GetFindHeadphonesAsync(default)).Enabled && fixture.Location.Requests == 1);
            fixture.Location.Permission = new(LocationAccess.Denied, "定位权限已拒绝。");
            await Throws(() => fixture.Backend.SetFindHeadphonesEnabledAsync(true, default));
            Check(!(await fixture.Backend.GetFindHeadphonesAsync(default)).Enabled);
            await Throws(() => fixture.Backend.SetDeviceLocationRecordingAsync(fixture.Identity, true, default));
            fixture.Location.Permission = new(LocationAccess.Allowed, "系统定位已允许。");
            fixture.Location.RequestedPermission = new(LocationAccess.Denied, "保存时的系统授权检查未通过。");
            await Throws(() => fixture.Backend.SetFindHeadphonesEnabledAsync(true, default));
            Check(!(await fixture.Backend.GetFindHeadphonesAsync(default)).Enabled);
            fixture.Location.RequestedPermission = null;
            await fixture.Backend.SetFindHeadphonesEnabledAsync(true, default);
            fixture.Backend.ObserveConnection(1, true); fixture.Backend.ObserveConnection(1, false);
            Check(fixture.Location.Calls == 0); // The per-device default is still off.
        });

        await test("OS disconnect records an unsupported model and survives a Host restart with stable device preferences", async () =>
        {
            string directory = NewDirectory(scratch);
            UiRecordedLocation? recorded;
            await using (var fixture = await Fixture.Create(directory, direct: true))
            {
                await fixture.Enable();
                fixture.Backend.ObserveConnection(1, false);
                var request = await fixture.Location.Next();
                request.SetResult(Fix(fixture.Time));
                await Until(() => fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Status == "recorded");
                var state = fixture.Backend.Subscribe().FindHeadphones!;
                recorded = state.Devices.Single().Location;
                Check(recorded is not null && recorded.DisconnectedAt == fixture.Time.GetUtcNow());
                Check(fixture.Backend.Subscribe().Snapshots.Single().SupportedModel == false);
                var text = await File.ReadAllTextAsync(Path.Combine(directory, "find-headphones.json"));
                Check(!text.Contains(fixture.Identity, StringComparison.Ordinal)
                    && text.Contains(DeviceWhitelistStore.KeyFor(1), StringComparison.Ordinal));
            }
            await using var restored = await Fixture.Create(directory, direct: true);
            var stateAfterRestart = await restored.Backend.GetFindHeadphonesAsync(default);
            Check(stateAfterRestart.Enabled && stateAfterRestart.Devices.Single().Enabled
                && stateAfterRestart.Devices.Single().Location == recorded && restored.Location.Calls == 0);
        });

        await test("a restarted opted-in Host verifies unknown permission only at a disconnect without requesting access", async () =>
        {
            string directory = NewDirectory(scratch);
            await using (var seed = await Fixture.Create(directory, direct: true))
                await seed.Enable();

            await using var fixture = await Fixture.Create(directory, direct: true);
            fixture.Location.ChangePermission(new(LocationAccess.Unknown, "将在耳机断开时核实定位权限。"));
            var state = await fixture.Backend.GetFindHeadphonesAsync(default);
            Check(state.Enabled && state.Devices.Single().Enabled && state.Permission.Status == "unknown"
                && fixture.Location.Calls == 0 && fixture.Location.Requests == 0);
            // Deferred verification must not weaken the explicit enabling / save gate.
            await Throws(() => fixture.Backend.SetFindHeadphonesEnabledAsync(true, default));
            Check(fixture.Location.Requests == 1);

            fixture.Backend.ObserveConnection(1, false);
            var request = await fixture.Location.Next();
            fixture.Location.ChangePermission(new(LocationAccess.Unknown, "将在耳机断开时核实定位权限。"));
            fixture.Location.ChangePermission(new(LocationAccess.Allowed, "系统定位已允许。"));
            request.SetResult(Fix(fixture.Time));
            await Until(() => fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Status == "recorded");
            var recorded = fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Location;
            Check(recorded is not null && fixture.Location.Calls == 1 && fixture.Location.Requests == 1);

            fixture.Location.ChangePermission(new(LocationAccess.Denied, "定位权限已拒绝。"));
            fixture.Backend.ObserveConnection(1, true);
            fixture.Backend.ObserveConnection(1, false);
            await Until(() => fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Status == "failed");
            Check(fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Location == recorded
                && fixture.Location.Calls == 1 && fixture.Location.Requests == 1);
        });

        await test("only distinct OS connected to disconnected transitions capture while worker pause sleep and radio events do not", async () =>
        {
            await using var fixture = await Fixture.Create(scratch);
            await fixture.Enable();
            fixture.Backend.Pause(fixture.Identity);
            fixture.Backend.SetSuspended(true);
            fixture.Backend.SetRadioAvailable(false);
            Check(fixture.Location.Calls == 0);
            fixture.Backend.ObserveConnection(1, false);
            var request = await fixture.Location.Next();
            fixture.Backend.ObserveConnection(1, false);
            request.SetResult(Fix(fixture.Time));
            await Until(() => fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Status == "recorded");
            Check(fixture.Location.Calls == 1);
            fixture.Backend.SetRadioAvailable(true); fixture.Backend.SetSuspended(false);
            Check(fixture.Location.Calls == 1);
        });

        await test("fresh catalog discovers a missed disconnect and a newer OS event wins over an in-flight catalog", async () =>
        {
            await using var fixture = await Fixture.Create(scratch);
            await fixture.Enable();
            fixture.Backend.ObserveConnection(1, true);
            fixture.Devices[0] = fixture.Devices[0] with { Connected = false };
            await fixture.Backend.RefreshCatalogAsync(default);
            (await fixture.Location.Next()).SetResult(Fix(fixture.Time));
            await Until(() => fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Status == "recorded");
            Check(fixture.Location.Calls == 1);
            var stale = new TaskCompletionSource<IReadOnlyList<BluetoothCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Enumeration = _ => stale.Task;
            var refresh = fixture.Backend.RefreshCatalogAsync(default);
            fixture.Backend.ObserveConnection(1, true);
            stale.SetResult(fixture.Devices.ToArray());
            await refresh;
            Check(fixture.Backend.Devices().Single().Connected && fixture.Location.Calls == 1);
        });

        await test("late and canceled location results cannot replace a newer disconnect or write after either switch is disabled", async () =>
        {
            await using var fixture = await Fixture.Create(scratch);
            await fixture.Enable();
            fixture.Backend.ObserveConnection(1, false);
            var first = await fixture.Location.Next();
            fixture.Backend.ObserveConnection(1, true); fixture.Backend.ObserveConnection(1, false);
            var second = await fixture.Location.Next();
            second.SetResult(Fix(fixture.Time) with { Latitude = 22 });
            await Until(() => fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Location?.Latitude == 22);
            first.SetResult(Fix(fixture.Time) with { Latitude = 11 });
            Check(fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Location?.Latitude == 22);
            fixture.Backend.ObserveConnection(1, true); fixture.Backend.ObserveConnection(1, false);
            var third = await fixture.Location.Next();
            await fixture.Backend.SetDeviceLocationRecordingAsync(fixture.Identity, false, default);
            third.SetResult(Fix(fixture.Time) with { Latitude = 33 });
            Check(fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Location?.Latitude == 22);
            await fixture.Backend.SetDeviceLocationRecordingAsync(fixture.Identity, true, default);
            fixture.Backend.ObserveConnection(1, true); fixture.Backend.ObserveConnection(1, false);
            var fourth = await fixture.Location.Next();
            await fixture.Backend.SetFindHeadphonesEnabledAsync(false, default);
            fourth.SetResult(Fix(fixture.Time) with { Latitude = 44 });
            Check(fixture.Backend.Subscribe().FindHeadphones!.Devices.Single() is { Enabled: true, Location.Latitude: 22 });
        });

        await test("stale invalid failed and timed-out OS fixes preserve the last successful location", async () =>
        {
            await using var fixture = await Fixture.Create(scratch);
            await fixture.Enable();
            async Task Capture(LocationFix value)
            {
                fixture.Backend.ObserveConnection(1, true); fixture.Backend.ObserveConnection(1, false);
                (await fixture.Location.Next()).SetResult(value);
                await Until(() => fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Status != "locating");
            }
            await Capture(Fix(fixture.Time));
            var expected = fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Location;
            await Capture(Fix(fixture.Time) with { Timestamp = fixture.Time.GetUtcNow() - TimeSpan.FromSeconds(61) });
            Check(fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Location == expected);
            await Capture(Fix(fixture.Time) with { Latitude = double.NaN });
            Check(fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Status == "failed");
            fixture.Backend.ObserveConnection(1, true); fixture.Backend.ObserveConnection(1, false);
            var pending = await fixture.Location.Next();
            fixture.Time.Advance(TimeSpan.FromSeconds(16));
            await Until(() => fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Status == "failed");
            pending.SetResult(Fix(fixture.Time) with { Latitude = 88 });
            Check(fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Location == expected);
            fixture.Backend.ObserveConnection(1, true); fixture.Backend.ObserveConnection(1, false);
            (await fixture.Location.Next()).SetException(new IOException("OS failure"));
            await Until(() => fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Status == "failed");
            Check(fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Location == expected);
        });

        await test("location freshness is relative to the disconnect and a sleeping timer cannot admit a wake-up fix", async () =>
        {
            var clock = new SleepingClock();
            await using var fixture = await Fixture.Create(scratch, time: clock);
            await fixture.Enable();
            DateTimeOffset disconnected = clock.GetUtcNow();
            fixture.Backend.ObserveConnection(1, false);
            var initial = await fixture.Location.Next();
            clock.Jump(TimeSpan.FromSeconds(14));
            // The fix was fresh at the event, although it is now older than sixty seconds.
            initial.SetResult(Fix(clock) with { Timestamp = disconnected - TimeSpan.FromSeconds(59) });
            await Until(() => fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Status == "recorded");
            var expected = fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Location;
            Check(expected?.PositionTimestamp == disconnected - TimeSpan.FromSeconds(59));

            disconnected = clock.GetUtcNow();
            fixture.Backend.ObserveConnection(1, true); fixture.Backend.ObserveConnection(1, false);
            var future = await fixture.Location.Next();
            clock.Jump(TimeSpan.FromSeconds(14));
            // This is less than five seconds ahead of now, but is outside the event's fifteen-second window.
            future.SetResult(Fix(clock) with { Timestamp = disconnected + TimeSpan.FromSeconds(16) });
            await Until(() => fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Status == "failed");
            Check(fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Location == expected);

            fixture.Backend.ObserveConnection(1, true); fixture.Backend.ObserveConnection(1, false);
            var wake = await fixture.Location.Next();
            clock.Jump(TimeSpan.FromSeconds(16)); // Wall clock moves while the timer has not fired.
            wake.SetResult(Fix(clock));
            await Until(() => fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Status == "failed");
            Check(fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Location == expected);
        });

        await test("permission revocation invalidates pending location capture and keeps the persisted opt-in", async () =>
        {
            await using var fixture = await Fixture.Create(scratch);
            await fixture.Enable();
            fixture.Backend.ObserveConnection(1, false);
            var pending = await fixture.Location.Next();
            fixture.Location.ChangePermission(new(LocationAccess.SystemDisabled, "系统定位已关闭。"));
            pending.SetResult(Fix(fixture.Time));
            var state = fixture.Backend.Subscribe().FindHeadphones!;
            Check(state.Enabled && state.Permission.Status == "systemDisabled"
                && state.Devices.Single() is { Location: null, Status: "failed" });
        });

        await test("device opt-ins and successful locations remain independent for multiple headphones", async () =>
        {
            await using var fixture = await Fixture.Create(scratch, secondDevice: true);
            var identities = fixture.Backend.Devices().ToDictionary(device => device.Name, device => device.Identity);
            string firstId = identities["未适配型号"], secondId = identities["另一副耳机"];
            await fixture.Backend.SetFindHeadphonesEnabledAsync(true, default);
            await fixture.Backend.SetDeviceLocationRecordingAsync(firstId, true, default);
            fixture.Backend.ObserveConnection(2, false);
            Check(fixture.Location.Calls == 0);
            await fixture.Backend.SetDeviceLocationRecordingAsync(secondId, true, default);
            fixture.Backend.ObserveConnection(2, true); fixture.Backend.ObserveConnection(2, false);
            var secondRequest = await fixture.Location.Next();
            fixture.Backend.ObserveConnection(1, false);
            var firstRequest = await fixture.Location.Next();
            await fixture.Backend.SetDeviceLocationRecordingAsync(firstId, false, default);
            firstRequest.SetResult(Fix(fixture.Time) with { Latitude = 11 });
            secondRequest.SetResult(Fix(fixture.Time) with { Latitude = 22 });
            await Until(() => fixture.Backend.Subscribe().FindHeadphones!.Devices.Single(device => device.Identity == secondId).Status == "recorded");
            var devices = fixture.Backend.Subscribe().FindHeadphones!.Devices;
            Check(devices.Single(device => device.Identity == firstId) is { Enabled: false, Location: null }
                && devices.Single(device => device.Identity == secondId) is { Enabled: true, Location.Latitude: 22 });
        });

        await test("revoking permission during a prepared enabling write never commits the opt-in", async () =>
        {
            string directory = NewDirectory(scratch);
            var store = new DelayedStore(Path.Combine(directory, "find-headphones.json"));
            await using var fixture = await Fixture.Create(directory, direct: true, store: store);
            store.PauseNextWrite();
            var enabling = fixture.Backend.SetFindHeadphonesEnabledAsync(true, default);
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Check(!store.Load().Enabled);
            fixture.Location.ChangePermission(new(LocationAccess.Denied, "定位权限已拒绝。"));
            store.Release.TrySetResult();
            await Throws(() => enabling);
            Check(!store.Load().Enabled && !fixture.Backend.Subscribe().FindHeadphones!.Enabled);
        });

        await test("slow location preparation leaves Bluetooth events responsive and disabling never commits a canceled write", async () =>
        {
            string directory = NewDirectory(scratch);
            var store = new DelayedStore(Path.Combine(directory, "find-headphones.json"));
            await using var fixture = await Fixture.Create(directory, direct: true, store: store);
            await fixture.Enable();
            store.PauseNextWrite();
            fixture.Backend.ObserveConnection(1, false);
            (await fixture.Location.Next()).SetResult(Fix(fixture.Time));
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var disabled = fixture.Backend.SetDeviceLocationRecordingAsync(fixture.Identity, false, default);
            await Task.Run(() => fixture.Backend.ObserveConnection(1, true)).WaitAsync(TimeSpan.FromSeconds(1));
            Check(!disabled.IsCompleted && fixture.Backend.Devices().Single().Connected
                && store.Load().Devices[DeviceWhitelistStore.KeyFor(1)].Location is null);
            store.Release.TrySetResult();
            await disabled;
            var saved = store.Load().Devices[DeviceWhitelistStore.KeyFor(1)];
            Check(!saved.Enabled && saved.Location is null
                && fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Location is null
                && !Directory.EnumerateFiles(directory, "find-headphones.json.*.tmp").Any());
        });

        await test("Host disposal drains prepared location writes without committing them", async () =>
        {
            string directory = NewDirectory(scratch);
            var store = new DelayedStore(Path.Combine(directory, "find-headphones.json"));
            await using var fixture = await Fixture.Create(directory, direct: true, store: store);
            await fixture.Enable();
            store.PauseNextWrite();
            fixture.Backend.ObserveConnection(1, false);
            (await fixture.Location.Next()).SetResult(Fix(fixture.Time));
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var disposal = fixture.Backend.DisposeAsync().AsTask();
            Check(!disposal.IsCompleted);
            store.Release.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(4));
            Check(store.Load().Devices[DeviceWhitelistStore.KeyFor(1)].Location is null
                && !Directory.EnumerateFiles(directory, "find-headphones.json.*.tmp").Any());
        });

        await test("a prepared write after the event deadline never commits a previously fresh fix", async () =>
        {
            string directory = NewDirectory(scratch);
            var clock = new SleepingClock();
            var store = new DelayedStore(Path.Combine(directory, "find-headphones.json"));
            await using var fixture = await Fixture.Create(directory, direct: true, store: store, time: clock);
            await fixture.Enable();
            store.PauseNextWrite();
            fixture.Backend.ObserveConnection(1, false);
            (await fixture.Location.Next()).SetResult(Fix(clock));
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            clock.Jump(TimeSpan.FromSeconds(16));
            store.Release.TrySetResult();
            await Until(() => fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Status == "failed");
            Check(store.Load().Devices[DeviceWhitelistStore.KeyFor(1)].Location is null);
        });

        await test("a failed atomic location commit preserves the original file and never reports a successful record", async () =>
        {
            string directory = NewDirectory(scratch);
            string path = Path.Combine(directory, "find-headphones.json");
            var store = new DelayedStore(path);
            await using var fixture = await Fixture.Create(directory, direct: true, store: store);
            await fixture.Enable();
            string before = await File.ReadAllTextAsync(path);
            store.PauseNextWrite();
            fixture.Backend.ObserveConnection(1, false);
            (await fixture.Location.Next()).SetResult(Fix(fixture.Time));
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            // Remove only this test's staged file to force File.Move to fail before replacing the original.
            File.Delete(Directory.EnumerateFiles(directory, "find-headphones.json.*.tmp").Single());
            store.Release.TrySetResult();
            await Until(() => fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Status == "failed");
            Check(await File.ReadAllTextAsync(path) == before
                && fixture.Backend.Subscribe().FindHeadphones!.Devices.Single().Location is null);
        });

        await test("location preferences fail closed on malformed data and a failed save never enables recording", async () =>
        {
            string directory = NewDirectory(scratch);
            await File.WriteAllTextAsync(Path.Combine(directory, "find-headphones.json"),
                "{\"schemaVersion\":1,\"enabled\":true,\"devices\":[null]}");
            await using var fixture = await Fixture.Create(directory, direct: true);
            Check(!(await fixture.Backend.GetFindHeadphonesAsync(default)).Enabled);
            File.Delete(Path.Combine(directory, "find-headphones.json"));
            Directory.CreateDirectory(Path.Combine(directory, "find-headphones.json"));
            await Throws(() => fixture.Backend.SetFindHeadphonesEnabledAsync(true, default));
            Check(!(await fixture.Backend.GetFindHeadphonesAsync(default)).Enabled);
        });

        await test("find headphones RPC provides subscription baseline settings and rejects schema 4", async () =>
        {
            await using var fixture = await Fixture.Create(scratch);
            string pipe = "HeyMelody.FindTests." + Guid.NewGuid().ToString("N");
            await using var server = new BackendPipeServer(fixture.Backend, pipe);
            server.Start();
            await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(4000);
            using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, true);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            async Task<BackendMessage> Send(BackendRequest request)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(request, RpcWire.Json));
                while (true)
                {
                    var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(4)) ?? throw new IOException();
                    var reply = JsonSerializer.Deserialize<BackendMessage>(line, RpcWire.Json)!;
                    if (reply.Id == request.Id && reply.Kind == "response") return reply;
                }
            }
            var subscribed = await Send(new() { Id = 1, Operation = "subscribe", Role = "main" });
            Check(subscribed.Data!.Value.Deserialize<BackendSubscription>(RpcWire.Json)!.FindHeadphones?.Enabled == false);
            Check((await Send(new() { Id = 2, Operation = "locationPermission" })).Success);
            var enabled = await Send(new() { Id = 3, Operation = "setFindHeadphones",
                Action = JsonSerializer.SerializeToElement(new FindHeadphonesEnabledArgs(true), RpcWire.Json) });
            Check(enabled.Data!.Value.Deserialize<UiFindHeadphonesState>(RpcWire.Json)!.Enabled);
            var deviceEnabled = await Send(new() { Id = 4, Operation = "setDeviceLocationRecording",
                BackendInstanceId = fixture.Backend.InstanceId, Identity = fixture.Identity,
                Action = JsonSerializer.SerializeToElement(new FindHeadphonesEnabledArgs(true), RpcWire.Json) });
            Check(deviceEnabled.Data!.Value.Deserialize<UiFindHeadphonesState>(RpcWire.Json)!.Devices.Single().Enabled);
            Check((await Send(new() { Id = 5, Operation = "findHeadphones" })).Success);
            Check((await Send(new() { Id = 6, Operation = "findHeadphones", SchemaVersion = 4 })).ErrorCode == "versionMismatch");
        });
    }

    private static string NewDirectory(string scratch)
    {
        string path = Path.Combine(scratch, "find-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static LocationFix Fix(TimeProvider time) => new(22.3, 114.2, 30, time.GetUtcNow());
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Find headphones assertion failed."); }
    private static async Task Throws(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected location operation failure.");
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly FakeLocation Location = new();
        internal readonly BackendTests.TestTime Time = new();
        internal readonly List<BluetoothCandidate> Devices = [new(1, "未适配型号", true, true, true, 0x404)];
        internal Func<CancellationToken, Task<IReadOnlyList<BluetoothCandidate>>>? Enumeration;
        internal BackendSupervisor Backend = null!;
        internal string Identity => Backend.Devices().Single().Identity;
        internal static async Task<Fixture> Create(string scratch, bool direct = false, FindHeadphonesStore? store = null,
            bool secondDevice = false, TimeProvider? time = null, Func<IPeerWorker>? workerFactory = null)
        {
            var result = new Fixture();
            if (secondDevice) result.Devices.Add(new(2, "另一副耳机", true, true, true, 0x404));
            string directory = direct ? scratch : NewDirectory(scratch);
            var whitelist = new DeviceWhitelistStore(Path.Combine(directory, "devices.json"));
            whitelist.Save(result.Devices.Select(device => DeviceWhitelistStore.KeyFor(device.Address)));
            result.Backend = new(whitelist, token => result.Enumeration?.Invoke(token)
                ?? Task.FromResult<IReadOnlyList<BluetoothCandidate>>(result.Devices.ToArray()),
                workerFactory ?? (() => new UnsupportedWorker()), time ?? result.Time, locationProvider: result.Location, findHeadphonesStore: store);
            await result.Backend.InitializeAsync(default);
            return result;
        }
        internal async Task Enable()
        {
            await Backend.SetFindHeadphonesEnabledAsync(true, default);
            await Backend.SetDeviceLocationRecordingAsync(Identity, true, default);
        }
        public ValueTask DisposeAsync() => Backend.DisposeAsync();
    }
    private sealed class FakeLocation : ILocationProvider
    {
        internal LocationPermission Permission = new(LocationAccess.Allowed, "系统定位已允许。");
        internal LocationPermission? RequestedPermission;
        internal int Calls, Requests;
        private readonly Channel<TaskCompletionSource<LocationFix>> _requests = Channel.CreateUnbounded<TaskCompletionSource<LocationFix>>();
        public event Action<LocationPermission>? PermissionChanged;
        public Task<LocationPermission> RequestPermissionAsync(CancellationToken token)
        { Interlocked.Increment(ref Requests); return Task.FromResult(RequestedPermission ?? Permission); }
        public Task<LocationPermission> CheckPermissionAsync(CancellationToken token) => Task.FromResult(Permission);
        public Task<LocationFix> GetLocationAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var source = new TaskCompletionSource<LocationFix>(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Writer.TryWrite(source);
            return source.Task; // Deliberately ignores cancellation to exercise late-result protection.
        }
        internal Task<TaskCompletionSource<LocationFix>> Next() =>
            _requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        internal void ChangePermission(LocationPermission permission)
        { Permission = permission; PermissionChanged?.Invoke(permission); }
        public void Dispose() { }
    }
    private sealed class UnsupportedWorker : IPeerWorker
    {
        public event Action<UiSnapshot>? SnapshotChanged { add { } remove { } }
        public event Action? Unavailable { add { } remove { } }
        private UiSnapshot _snapshot = new();
        public Task<UiSnapshot> InitializeAsync(ulong address, string identity, string name, CancellationToken token) =>
            Task.FromResult(_snapshot = new() { Identity = identity, DeviceName = name, Connected = true,
                SupportedModel = false, ConnectionState = ConnectionState.Connected, Revision = 1 });
        public Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken token) => Task.FromResult(_snapshot);
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class DelayedStore(string path) : FindHeadphonesStore(path)
    {
        private int _pause;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void PauseNextWrite() => Interlocked.Exchange(ref _pause, 1);
        internal override async Task<IPreparedLocationWrite> PrepareAsync(bool enabled,
            IReadOnlyDictionary<string, DeviceLocationPreference> devices, CancellationToken token)
        {
            var prepared = await base.PrepareAsync(enabled, devices, token);
            if (Interlocked.Exchange(ref _pause, 0) != 0)
            {
                Entered.TrySetResult();
                await Release.Task; // Simulates a flushed temporary file whose completion arrives after cancellation.
            }
            return prepared;
        }
    }
    private sealed class SleepingClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Jump(TimeSpan duration) => _now += duration;
        // CreateTimer deliberately uses real elapsed time while the simulated sleeping wall clock jumps.
    }
}
