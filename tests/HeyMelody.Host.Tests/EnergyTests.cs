using HeyMelody.Platform;
#if HEYMELODY_WINDOWS
using PowerThrottlingState = HeyMelodyWindows.Windows.PowerThrottlingState;
#endif
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;

// EnergyPolicy, EnergyController, the GUI count kept by the pipe server and the Windows struct layout. Nothing here touches
// a real process priority: the applier is a fake, and the Windows-only audition check never reaches WASAPI.
internal static class EnergyTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        await test("energy policy: 15 s startup grace in Default, then Eco without a window", () =>
        {
            var clock = new FakeClock(); using var policy = new EnergyPolicy(clock); var changes = Track(policy);
            Check(policy.Mode == EnergyMode.Default, "Startup was not Default");
            clock.Advance(TimeSpan.FromSeconds(14.9)); Check(policy.Mode == EnergyMode.Default && changes.Count == 0, "Grace ended early");
            clock.Advance(TimeSpan.FromSeconds(0.2));
            Check(policy.Mode == EnergyMode.Eco && changes.SequenceEqual([EnergyMode.Eco]), "Grace did not end in Eco");
            return Task.CompletedTask;
        });
        await test("energy policy: a connected window restores Default at once and the last one leaving lingers 10 s", () =>
        {
            var clock = new FakeClock(); using var policy = new EnergyPolicy(clock); var changes = Track(policy);
            clock.Advance(TimeSpan.FromSeconds(20)); Check(policy.Mode == EnergyMode.Eco, "Setup did not reach Eco");
            policy.SetGuiCount(1);
            Check(policy.Mode == EnergyMode.Default && changes.SequenceEqual([EnergyMode.Eco, EnergyMode.Default]), "Window did not restore Default synchronously");
            clock.Advance(TimeSpan.FromMinutes(5)); Check(policy.Mode == EnergyMode.Default, "A connected window timed out");
            policy.SetGuiCount(2); policy.SetGuiCount(1); clock.Advance(TimeSpan.FromMinutes(1)); Check(policy.Mode == EnergyMode.Default, "One of two windows leaving started a linger");
            policy.SetGuiCount(0);
            clock.Advance(TimeSpan.FromSeconds(9.9)); Check(policy.Mode == EnergyMode.Default, "Linger ended early");
            clock.Advance(TimeSpan.FromSeconds(0.2)); Check(policy.Mode == EnergyMode.Eco, "Linger did not end in Eco");
            return Task.CompletedTask;
        });
        await test("energy policy: a reconnect inside the linger never reaches Eco and each departure lingers anew", () =>
        {
            var clock = new FakeClock(); using var policy = new EnergyPolicy(clock); var changes = Track(policy);
            clock.Advance(TimeSpan.FromSeconds(20));
            policy.SetGuiCount(1); policy.SetGuiCount(0); clock.Advance(TimeSpan.FromSeconds(8));
            policy.SetGuiCount(1); clock.Advance(TimeSpan.FromSeconds(30)); policy.SetGuiCount(0);
            clock.Advance(TimeSpan.FromSeconds(9)); Check(policy.Mode == EnergyMode.Default, "The earlier linger ended the new one");
            clock.Advance(TimeSpan.FromSeconds(2));
            Check(policy.Mode == EnergyMode.Eco && changes.SequenceEqual([EnergyMode.Eco, EnergyMode.Default, EnergyMode.Eco]), "Reconnect thrashed the mode");
            // A window that comes and goes during the startup grace cannot shorten it.
            var early = new FakeClock(); using var startup = new EnergyPolicy(early);
            early.Advance(TimeSpan.FromSeconds(3)); startup.SetGuiCount(1); early.Advance(TimeSpan.FromSeconds(2)); startup.SetGuiCount(0);
            early.Advance(TimeSpan.FromSeconds(9.9)); Check(startup.Mode == EnergyMode.Default, "Linger after an early window ended before the grace");
            early.Advance(TimeSpan.FromSeconds(0.2)); Check(startup.Mode == EnergyMode.Eco, "Grace plus linger did not end in Eco");
            return Task.CompletedTask;
        });
        await test("energy policy: a launch hold forces Default for 30 s and a window opening inside it keeps it", () =>
        {
            var clock = new FakeClock(); using var policy = new EnergyPolicy(clock); var changes = Track(policy);
            clock.Advance(TimeSpan.FromSeconds(20));
            policy.HoldForLaunch(); Check(policy.Mode == EnergyMode.Default && changes.Count == 2, "Launch hold did not restore Default synchronously");
            clock.Advance(TimeSpan.FromSeconds(29.9)); Check(policy.Mode == EnergyMode.Default, "Launch hold expired early");
            clock.Advance(TimeSpan.FromSeconds(0.2)); Check(policy.Mode == EnergyMode.Eco, "Launch hold never expired");
            policy.HoldForLaunch(); clock.Advance(TimeSpan.FromSeconds(5)); policy.SetGuiCount(1); clock.Advance(TimeSpan.FromMinutes(2));
            Check(policy.Mode == EnergyMode.Default, "A window opened in the hold did not keep Default");
            return Task.CompletedTask;
        });
        await test("energy controller: attach applies the current Eco mode at once and later changes run off the caller's thread", async () =>
        {
            var clock = new FakeClock(); using var policy = new EnergyPolicy(clock); clock.Advance(TimeSpan.FromSeconds(20));
            var applier = new FakeApplier(); using var controller = new EnergyController(policy, applier);
            using var self = Process.GetCurrentProcess();
            using var attached = controller.Attach(self);
            Check(applier.Calls.ToArray() is [(_, EnergyMode.Eco, _)], "Attach did not apply Eco synchronously");
            int caller = Environment.CurrentManagedThreadId;
            policy.SetGuiCount(1);
            await Until(() => applier.Calls.Count == 2);
            Check(applier.Calls.ToArray()[1] is (_, EnergyMode.Default, var thread) && thread != caller, "Mode change was not applied on a pool thread");
            // A count change that keeps the mode raises no event, so nothing is applied again.
            policy.SetGuiCount(2); await Task.Delay(100); Check(applier.Calls.Count == 2, "An unchanged mode was applied again");
        });
        await test("energy controller: exited and detached processes are skipped and a launch is prepared synchronously", async () =>
        {
            var clock = new FakeClock(); using var policy = new EnergyPolicy(clock); clock.Advance(TimeSpan.FromSeconds(20));
            var applier = new FakeApplier(); using var controller = new EnergyController(policy, applier);
            using var exited = Process.Start(ExitingProcess()) ?? throw new IOException(); await exited.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var gone = controller.Attach(exited);
            Check(applier.Calls.IsEmpty, "An exited process was given a mode");
            using var self = Process.GetCurrentProcess(); var detaching = controller.Attach(self);
            Check(applier.Calls.Count == 1, "Live attach did not apply");
            controller.PrepareLaunch();
            // Whether the caller or the change notification's pool thread applies it, Default is in place when PrepareLaunch returns.
            Check(policy.Mode == EnergyMode.Default && applier.Calls.ToArray() is [_, (_, EnergyMode.Default, _)], "PrepareLaunch returned before Default was applied");
            detaching.Dispose(); clock.Advance(TimeSpan.FromMinutes(1)); await Task.Delay(100);
            Check(applier.Calls.Count == 2, "A detached or exited process was applied after the mode went back to Eco");
        });
        await test("energy controller: failures are recorded once per process and capability, never thrown and never reported as health", async () =>
        {
            var clock = new FakeClock(); using var policy = new EnergyPolicy(clock); clock.Advance(TimeSpan.FromSeconds(20));
            var applier = new FakeApplier { Fail = (report, mode) => { report("powerThrottling", 5); if (mode == EnergyMode.Eco) report("priorityClass", 87); } };
            using var controller = new EnergyController(policy, applier); using var self = Process.GetCurrentProcess();
            using var attached = controller.Attach(self);
            for (int i = 0; i < 3; i++)
            {
                int before = applier.Calls.Count; policy.SetGuiCount(1); await Until(() => applier.Calls.Count == before + 1);
                before = applier.Calls.Count; policy.SetGuiCount(0); clock.Advance(TimeSpan.FromSeconds(11)); await Until(() => applier.Calls.Count == before + 1);
            }
            Check(controller.Failures == 2 && controller.LastError is 5 or 87, $"Failures were not recorded once per capability ({controller.Failures})");
            // An applier that throws is contained as well; the failure is one more capability of the same process.
            var throwing = new FakeApplier { Throw = true };
            using var second = new EnergyController(policy, throwing); using var again = second.Attach(self);
            policy.SetGuiCount(1); await Until(() => throwing.Calls.Count >= 2);
            Check(second.Failures == 1, "A throwing applier was not recorded exactly once");
        });
#if HEYMELODY_WINDOWS
        await test("POWER_THROTTLING_STATE is 12 bytes with fields at 0, 4 and 8", () =>
        {
            Check(Marshal.SizeOf<PowerThrottlingState>() == 12, "Struct size");
            Check(Marshal.OffsetOf<PowerThrottlingState>(nameof(PowerThrottlingState.Version)) == 0
                && Marshal.OffsetOf<PowerThrottlingState>(nameof(PowerThrottlingState.ControlMask)) == 4
                && Marshal.OffsetOf<PowerThrottlingState>(nameof(PowerThrottlingState.StateMask)) == 8, "Field offsets");
            return Task.CompletedTask;
        });
#else
        Console.WriteLine("SKIP Windows POWER_THROTTLING_STATE layout");
#endif
        await test("pipe server counts every window role and still-unsubscribed clients, but not observers", async () =>
        {
            var clock = new FakeClock(); using var policy = new EnergyPolicy(clock); clock.Advance(TimeSpan.FromSeconds(20));
            Check(policy.Mode == EnergyMode.Eco, "Setup did not reach Eco");
            string directory = Path.Combine(scratch, "energy-pipe"); Directory.CreateDirectory(directory);
            await using var backend = new BackendSupervisor(new DeviceWhitelistStore(Path.Combine(directory, "devices.json")),
                _ => Task.FromResult<IReadOnlyList<BluetoothCandidate>>([]), () => throw new InvalidOperationException());
            string pipeName = "HeyMelody-energy-test-" + Guid.NewGuid().ToString("N");
            await using var server = new BackendPipeServer(backend, pipeName, policy); server.Start();
            await using var silent = await RawClient.Connect(pipeName);
            await Until(() => policy.GuiCount == 1);
            Check(policy.Mode == EnergyMode.Default, "An accepted client without a role did not count as a window");
            await using var observer = await RawClient.Connect(pipeName); await observer.Subscribe(1, "observer");
            await Until(() => policy.GuiCount == 1);
            var roles = new[] { "main", "manager", "calibration", "preview" }; var clients = new List<RawClient>();
            foreach (var role in roles) { var client = await RawClient.Connect(pipeName); clients.Add(client); await client.Subscribe(1, role); }
            await Until(() => policy.GuiCount == 5); await Task.Delay(100);
            Check(policy.GuiCount == 5, "An observer was counted as a window");
            foreach (var client in clients) await client.DisposeAsync();
            await silent.DisposeAsync();
            await Until(() => policy.GuiCount == 0);
            clock.Advance(TimeSpan.FromSeconds(9)); Check(policy.Mode == EnergyMode.Default, "Windows leaving did not linger");
            clock.Advance(TimeSpan.FromSeconds(2)); Check(policy.Mode == EnergyMode.Eco, "An observer kept the backend in Default");
        });
        await test("pipe server stops counting a client that never subscribes after the subscribe grace", async () =>
        {
            var clock = new FakeClock(); using var policy = new EnergyPolicy(clock); clock.Advance(TimeSpan.FromSeconds(20));
            string directory = Path.Combine(scratch, "energy-grace"); Directory.CreateDirectory(directory);
            await using var backend = new BackendSupervisor(new DeviceWhitelistStore(Path.Combine(directory, "devices.json")),
                _ => Task.FromResult<IReadOnlyList<BluetoothCandidate>>([]), () => throw new InvalidOperationException());
            string pipeName = "HeyMelody-energy-grace-" + Guid.NewGuid().ToString("N");
            await using var server = new BackendPipeServer(backend, pipeName, policy); server.Start();
            await using var silent = await RawClient.Connect(pipeName);
            await using var prompt = await RawClient.Connect(pipeName);
            await Until(() => policy.GuiCount == 2);
            clock.Advance(BackendPipeServer.SubscribeGrace - TimeSpan.FromSeconds(1));
            await prompt.Subscribe(1, "main");
            Check(policy.GuiCount == 2 && policy.Mode == EnergyMode.Default, "A client was dropped from the count before the grace ended");
            clock.Advance(TimeSpan.FromSeconds(2));
            await Until(() => policy.GuiCount == 1); await Task.Delay(100);
            Check(policy.GuiCount == 1, "The grace lapse affected a subscribed window");
            await prompt.DisposeAsync(); await Until(() => policy.GuiCount == 0);
            clock.Advance(EnergyPolicy.Linger + TimeSpan.FromSeconds(1));
            Check(policy.Mode == EnergyMode.Eco, "A silent client kept the backend out of Eco");
            await silent.Subscribe(1, "manager");
            await Until(() => policy.GuiCount == 1);
            Check(policy.Mode == EnergyMode.Default, "A late subscription after the grace was not counted as a window");
        });
        await test("idle audition thread blocks without polling, applies the thread exemption once and disposes within 2 s", async () =>
        {
#if HEYMELODY_WINDOWS
            if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP idle audition thread blocks without polling, applies the thread exemption once and disposes within 2 s"); return; }
            int exempted = 0;
            var player = new WasapiCalibrationAuditionPlayer(() => 0, () => { }, () => { Interlocked.Increment(ref exempted); return true; });
            await Until(() => Volatile.Read(ref exempted) == 1);
            await Task.Delay(200);
            await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Check(Volatile.Read(ref exempted) == 1, "The exemption ran more than once");
#else
            await Task.CompletedTask;
            Console.WriteLine("SKIP Windows WASAPI thread lifecycle");
#endif
        });
    }

    private static List<EnergyMode> Track(EnergyPolicy policy)
    {
        var changes = new List<EnergyMode>(); policy.ModeChanged += changes.Add; return changes;
    }
    private static ProcessStartInfo ExitingProcess() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false }
        : new ProcessStartInfo("/bin/sh", ["-c", "exit 0"]) { UseShellExecute = false };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(10, budget.Token);
    }

    private sealed class FakeApplier : IEnergyApplier
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<(Process Process, EnergyMode Mode, int Thread)> Calls = new();
        internal Action<Action<string, int>, EnergyMode>? Fail;
        internal bool Throw;
        public void Apply(Process process, EnergyMode mode, Action<string, int> failed)
        {
            Calls.Enqueue((process, mode, Environment.CurrentManagedThreadId));
            Fail?.Invoke(failed, mode);
            if (Throw) throw new InvalidOperationException("Synthetic applier failure");
        }
    }
    // Monotonic fake clock: timers are one-shot and fire in due order during Advance; Change re-arms, Infinite disarms.
    private sealed class FakeClock : TimeProvider
    {
        private long _ticks;
        private readonly List<FakeTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state); timer.Change(dueTime, period);
            lock (_timers) _timers.Add(timer); return timer;
        }
        internal void Advance(TimeSpan span)
        {
            long target = _ticks + span.Ticks;
            while (true)
            {
                FakeTimer? next; lock (_timers) next = _timers.Where(t => !t.Disposed && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null) break;
                _ticks = Math.Max(_ticks, next.Due); next.Due = long.MaxValue; next.Callback(next.State);
            }
            _ticks = target;
        }
        private sealed class FakeTimer(FakeClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal long Due = long.MaxValue; internal volatile bool Disposed;
            internal readonly TimerCallback Callback = callback; internal readonly object? State = state;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks; return true; }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    internal sealed class RawClient(NamedPipeClientStream pipe) : IAsyncDisposable
    {
        private readonly StreamWriter _writer = new(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        private RpcLineReader? _lines;
        internal static async Task<RawClient> Connect(string name)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(3000); return new(pipe);
        }
        internal async Task Subscribe(long id, string role)
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(new BackendRequest { Id = id, Operation = "subscribe", Role = role }, RpcWire.Json));
            await ReadUntil(line => JsonSerializer.Deserialize<BackendMessage>(line, RpcWire.Json)!.Id == id);
        }
        internal async Task<string> ReadUntil(Func<string, bool> match)
        {
            _lines ??= new(pipe);
            for (int i = 0; i < 40; i++)
            {
                string line = await _lines.ReadLineAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) ?? throw new IOException("Pipe closed");
                if (match(line)) return line;
            }
            throw new InvalidOperationException("Expected pipe line not found");
        }
        private int _disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await _writer.DisposeAsync(); } catch (IOException) { } catch (ObjectDisposedException) { }
            await pipe.DisposeAsync();
        }
    }
}
