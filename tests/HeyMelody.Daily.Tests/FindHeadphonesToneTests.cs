using System.Text.Json;
using System.Threading.Channels;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Devices;

internal static class FindHeadphonesToneTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("find tone requires exact profile, app condition, command and confirmation", GateAsync);
        await test("find tone uses exact bytes and matched ACKs; manual stop cancels 50-second stop", ManualAsync);
        await test("find tone stops at 50 seconds and reserves the original session", DeadlineAsync);
        await test("find tone reacts to SPP wear, with only one warning override", WearAsync);
        await test("find tone ignores placement retained from a rotated SPP session", RotatedWearAsync);
        await test("find tone disconnect and timeout are unconfirmed and never replayed", LostAsync);
        await test("find tone stop rejection stays unconfirmed without retry", RejectedStopAsync);
        await test("find tone graceful disposal stops exactly once in the original session", DisposeAsync);
    }

    private static async Task GateAsync()
    {
        foreach (var (functions, bits, minimum) in new[]
        {
            ("\"findDevice\":2", 0x30, 0),
            ("\"heyFindDevice\":1", 0x10, 0),
            ("\"findDevice\":1", 0x30, 17006005)
        })
        {
            var device = Peer((byte)bits);
            await using var client = Client(device, Profile(functions, minimum));
            var snapshot = await client.RefreshAsync();
            Require(snapshot.Connected && (minimum != 0 || snapshot.ProfileVerified) && !snapshot.Capabilities.FindHeadphonesTone);
            Require(!(await Control(client, snapshot, true)).Submitted && device.Writes.Count == 0);
        }
        var supported = Peer();
        await using var live = Client(supported, Profile("\"findDevice\":1"));
        var ready = await live.RefreshAsync();
        Require(ready.Capabilities.FindHeadphonesTone);
        Require(!(await live.SetFindHeadphonesToneAsync(ready.FindHeadphonesToneSession, true, false, false, default)).Submitted);
        Require(!(await live.SetFindHeadphonesToneAsync(ready.FindHeadphonesToneSession + 1, true, true, false, default)).Submitted);
        Require(supported.Writes.Count == 0);
    }

    private static async Task ManualAsync()
    {
        var device = Peer();
        var clock = new RingClock();
        await using var client = Client(device, Profile("\"heyFindDevice\":1"), clock);
        var snapshot = await client.RefreshAsync();
        device.SendOldSequenceFirst = true;
        device.SetterStatus = 3;
        Require((await Control(client, snapshot, true)).AckStatus == 3);
        Require(clock.ArmedFor is null);
        device.SetterStatus = 0;
        Require((await Control(client, snapshot, true)).After?.FindHeadphonesTonePhase == DailyFindHeadphonesTonePhase.Ringing);
        Require(!(await Control(client, snapshot, true)).Submitted);
        Require((await Control(client, snapshot, false)).After?.FindHeadphonesTonePhase == DailyFindHeadphonesTonePhase.Idle);
        clock.Advance(TimeSpan.FromMinutes(1));
        Frames(device, 1, 1, 0);
        Require(device.Opens == 1 && device.MaximumReaders == 1);
    }

    private static async Task DeadlineAsync()
    {
        var device = Peer();
        var clock = new RingClock();
        await using var client = Client(device, Profile("\"findDevice\":1"), clock);
        var states = Observe(client);
        var snapshot = await client.RefreshAsync();
        Require((await Control(client, snapshot, true)).AckStatus == 0);
        Require(clock.ArmedFor == TimeSpan.FromSeconds(50));
        var requests = device.Requests.Count;
        Require((await client.RefreshAsync()).FindHeadphonesTonePhase == DailyFindHeadphonesTonePhase.Ringing);
        Require(!(await client.SetDualConnectionAsync(true)).SubmissionAttempted && device.Requests.Count == requests);
        clock.Advance(TimeSpan.FromSeconds(49));
        Frames(device, 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        await PhaseAsync(states, DailyFindHeadphonesTonePhase.Stopping);
        await PhaseAsync(states, DailyFindHeadphonesTonePhase.Idle);
        Frames(device, 1, 0);
        Require(device.Opens == 1);
    }

    private static async Task WearAsync()
    {
        var device = Peer();
        device.ReadReplies[0x0109] = [0, 2, 1, 3, 2, 1];
        var clock = new RingClock();
        await using var client = Client(device, Profile("\"findDevice\":1,\"wearDetection\":2"), clock);
        var states = Observe(client);
        var snapshot = await client.RefreshAsync();
        Require(snapshot.FindHeadphonesWorn);
        Require(!(await Control(client, snapshot, true)).Submitted && device.Writes.Count == 0);
        var overridden = await Control(client, snapshot, true, worn: true);
        Require(overridden.AckStatus == 0);
        device.Push(0x0204, [2, 1, 1, 3]);
        await ObserveAsync(states, item => item.Revision > overridden.After!.Revision && item.FindHeadphonesWorn
            && item.FindHeadphonesTonePhase == DailyFindHeadphonesTonePhase.Ringing);
        Require((await Control(client, snapshot, false)).AckStatus == 0);

        device.Push(0x0204, [2, 1, 1, 1]);
        await ObserveAsync(states, item => !item.FindHeadphonesWorn && item.FindHeadphonesTonePhase == DailyFindHeadphonesTonePhase.Idle);
        Require((await Control(client, snapshot, true)).AckStatus == 0);
        device.Push(0x0204, [2, 1, 2, 3]);
        await PhaseAsync(states, DailyFindHeadphonesTonePhase.Stopping);
        await PhaseAsync(states, DailyFindHeadphonesTonePhase.Idle);
        Frames(device, 1, 0, 1, 0);

        var noWearCommand = Peer(0x20);
        await using var noWear = Client(noWearCommand, Profile("\"heyFindDevice\":1,\"wearDetection\":1"));
        var noWearSnapshot = await noWear.RefreshAsync();
        Require(noWearSnapshot.Capabilities.FindHeadphonesTone && !noWearSnapshot.Capabilities.FindHeadphonesWearCheck);
        var realme = Peer();
        realme.ReadReplies[0x0109] = [0, 1, 2, 3];
        await using var realmeClient = Client(realme, Profile("\"findDevice\":1", brand: "realme"));
        Require((await realmeClient.RefreshAsync()).FindHeadphonesWorn);
    }

    private static async Task LostAsync()
    {
        var device = Peer();
        var clock = new RingClock();
        await using var client = Client(device, Profile("\"findDevice\":1"), clock);
        var states = Observe(client);
        var snapshot = await client.RefreshAsync();
        Require((await Control(client, snapshot, true)).AckStatus == 0);
        device.Disconnect();
        var interrupted = await PhaseAsync(states, DailyFindHeadphonesTonePhase.Unconfirmed);
        Require(!interrupted.Connected);
        var restarted = await client.RefreshAsync();
        Require(restarted.Connected && restarted.FindHeadphonesToneSession != snapshot.FindHeadphonesToneSession);
        clock.Advance(TimeSpan.FromMinutes(1));
        Require(!(await Control(client, snapshot, false)).Submitted);
        Frames(device, 1);
        device.Silent.Add(0x0400);
        var timedOut = await Control(client, restarted, true);
        Require(timedOut.Submitted && timedOut.AckStatus is null && timedOut.Message.Contains("未确认"));
        Frames(device, 1, 1);
        Require(device.Opens == 2);
    }

    private static async Task RotatedWearAsync()
    {
        var device = Peer();
        device.ReadReplies[0x0109] = [0, 1, 1, 3];
        await using var client = Client(device, Profile("\"findDevice\":1,\"wearDetection\":1"));
        var initial = await client.RefreshAsync();
        Require(initial.FindHeadphonesWorn);
        var rotated = initial;
        for (var index = 0; index < 250 && rotated.FindHeadphonesToneSession == initial.FindHeadphonesToneSession; index++)
            rotated = await client.RefreshAsync(DailyRefreshScope.Battery);
        Require(rotated.FindHeadphonesToneSession != initial.FindHeadphonesToneSession && rotated.LeftInEar == true && !rotated.FindHeadphonesWorn);
        Require(!(await Control(client, initial, true, worn: true)).Submitted);
        var observed = await client.RefreshAsync(DailyRefreshScope.Information);
        Require(observed.FindHeadphonesWorn);
        Require(device.Writes.Count == 0);
    }

    private static async Task DisposeAsync()
    {
        var device = Peer();
        var clock = new RingClock();
        await using (var client = Client(device, Profile("\"findDevice\":1"), clock))
        {
            var snapshot = await client.RefreshAsync();
            Require((await Control(client, snapshot, true)).AckStatus == 0);
        }
        clock.Advance(TimeSpan.FromMinutes(1));
        Frames(device, 1, 0);
        Require(device.Opens == 1);
    }

    private static async Task RejectedStopAsync()
    {
        var device = Peer();
        var clock = new RingClock();
        await using var client = Client(device, Profile("\"findDevice\":1"), clock);
        var snapshot = await client.RefreshAsync();
        Require((await Control(client, snapshot, true)).AckStatus == 0);
        device.SetterStatus = 3;
        var rejected = await Control(client, snapshot, false);
        Require(rejected is { Submitted: true, AckStatus: 3, After.FindHeadphonesTonePhase: DailyFindHeadphonesTonePhase.Unconfirmed }
            && rejected.Message.Contains("未确认"));
        clock.Advance(TimeSpan.FromMinutes(1));
        Require(!(await Control(client, snapshot, true)).Submitted);
        Frames(device, 1, 0);
    }

    private static Fake Peer(byte capabilities = 0x30) => new()
    {
        CapabilityOverride = [capabilities],
        ReadReplies = { [0x0109] = [0, 2, 1, 1, 2, 1] }
    };

    private static DeviceProfile Profile(string functions, int minimum = 0, string brand = "oppo")
    {
        using var json = JsonDocument.Parse($$"""
            {"id":"06EC10","name":"Fixture","brand":"{{brand}}","supportSpp":true,
             "uuid":"00001101-0000-1000-8000-00805f9b34fb","minVersion":{{minimum}},"function":{ {{functions}} } }
            """);
        return DeviceProfile.FromResource(json.RootElement);
    }

    private static DailyClient Client(Fake device, DeviceProfile profile, TimeProvider? clock = null) =>
        new(device, product => product == profile.ProductId ? profile : null, timeProvider: clock);

    private static Task<DailyFindHeadphonesToneResult> Control(DailyClient client, DailySnapshot snapshot, bool play, bool worn = false) =>
        client.SetFindHeadphonesToneAsync(snapshot.FindHeadphonesToneSession, play, play, worn, default);

    private static ChannelReader<DailySnapshot> Observe(DailyClient client)
    {
        var results = Channel.CreateUnbounded<DailySnapshot>();
        client.SnapshotChanged += snapshot => results.Writer.TryWrite(snapshot);
        return results.Reader;
    }

    private static Task<DailySnapshot> PhaseAsync(ChannelReader<DailySnapshot> states, DailyFindHeadphonesTonePhase phase) =>
        ObserveAsync(states, item => item.FindHeadphonesTonePhase == phase);

    private static async Task<DailySnapshot> ObserveAsync(ChannelReader<DailySnapshot> states, Func<DailySnapshot, bool> matches)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var state = await states.ReadAsync(deadline.Token);
            if (matches(state)) return state;
        }
    }

    private static void Frames(Fake device, params byte[] expected) =>
        Require(device.Writes.All(packet => packet.Command == 0x0400 && packet.Payload.Length == 1)
            && device.Writes.Select(packet => packet.Payload[0]).SequenceEqual(expected));

    private static void Require(bool condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string detail = "")
    {
        if (!condition) throw new InvalidOperationException(detail);
    }

    private sealed class RingClock : TimeProvider
    {
        private Timer? _timer;
        internal TimeSpan? ArmedFor => _timer?.Remaining;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _timer = new(callback, state, dueTime);

        internal void Advance(TimeSpan interval)
        {
            if (_timer is { } timer && timer.Elapse(interval)) _timer = null;
        }

        private sealed class Timer(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            private int _disposed;
            internal TimeSpan Remaining { get; private set; } = due;
            internal bool Elapse(TimeSpan interval)
            {
                if (Volatile.Read(ref _disposed) != 0) return true;
                Remaining -= interval;
                if (Remaining > TimeSpan.Zero) return false;
                if (Interlocked.Exchange(ref _disposed, 1) == 0) callback(state);
                return true;
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
