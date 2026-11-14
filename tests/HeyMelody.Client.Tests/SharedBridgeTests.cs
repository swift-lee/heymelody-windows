using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;

internal static class SharedBridgeTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("shared location preferences work for an offline unknown model without a control session", async () =>
        {
            string pipe = Name();
            var initial = new UiFindHeadphonesState(false, new("unknown", ""), [], 1);
            var recorded = new UiRecordedLocation(22.3, 114.2, 80, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            var updated = new UiFindHeadphonesState(true, new("allowed", ""),
                [new("unknown-offline", true, recorded, "recorded", "")], 3);
            var pushed = new TaskCompletionSource<UiFindHeadphonesState>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new Server(pipe, async (reader, writer, token) =>
            {
                var subscribe = await Read(reader, token);
                await Reply(writer, subscribe, new BackendSubscription { BackendInstanceId = "location-host", FindHeadphones = initial }, token);
                var permission = await Read(reader, token);
                Check(permission.Operation == "locationPermission" && permission.BackendInstanceId == "location-host");
                await Reply(writer, permission, new UiLocationPermission("allowed", ""), token);
                var global = await Read(reader, token);
                Check(global.Operation == "setFindHeadphones" && global.Action.GetProperty("enabled").GetBoolean());
                await Reply(writer, global, updated with { Devices = [], Revision = 2 }, token);
                var device = await Read(reader, token);
                Check(device.Operation == "setDeviceLocationRecording" && device.Identity == "unknown-offline"
                    && device.DeviceGeneration is null && device.BackendInstanceId == "location-host"
                    && device.Action.GetProperty("enabled").GetBoolean());
                await Reply(writer, device, updated, token);
                var read = await Read(reader, token);
                Check(read.Operation == "findHeadphones");
                await Reply(writer, read, updated, token);
                await Send(writer, new BackendMessage { Kind = "event", Event = new BackendEvent
                    { Kind = "findHeadphones", BackendInstanceId = "location-host", FindHeadphones = updated } }, token);
                await Drain(reader, token);
            });
            await using var bridge = new ClientBridge(pipe, testBudget: TimeSpan.FromSeconds(5));
            bridge.EventReceived += notification =>
            { if (notification.FindHeadphones is { } state) pushed.TrySetResult(state); };
            Check((await bridge.SubscribeAsync("main", default)).FindHeadphones?.Enabled == false);
            Check((await bridge.RequestLocationPermissionAsync(default)).Status == "allowed");
            Check((await bridge.SetFindHeadphonesEnabledAsync(true, default)).Enabled);
            Check((await bridge.SetDeviceLocationRecordingAsync("unknown-offline", true, default)).Devices.Single().Enabled);
            Check((await bridge.GetFindHeadphonesAsync(default)).Devices.Single().Location == recorded);
            Check((await pushed.Task.WaitAsync(TimeSpan.FromSeconds(3))).Devices.Single().Location == recorded);
        });
        await test("shared pipe routes concurrent responses and preserves newer push generation", async () =>
        {
            var pushed = new TaskCompletionSource<UiSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            string pipe = Name();
            await using var server = new Server(pipe, async (reader, writer, token) =>
            {
                var subscribe = await Read(reader, token);
                Check(subscribe.Operation == "subscribe" && subscribe.Role == "main");
                await Reply(writer, subscribe, new BackendSubscription { BackendInstanceId = "instance" }, token);
                var select = await Read(reader, token);
                Check(select.Operation == "select" && select.BackendInstanceId == "instance");
                await Reply(writer, select, Snapshot(7, 10), token);
                var first = await Read(reader, token);
                var second = await Read(reader, token);
                var apply = first.Operation == "apply" ? first : second;
                var enumerate = first.Operation == "enumerate" ? first : second;
                Check(apply.Identity == "peer" && apply.DeviceGeneration == 7 && apply.BackendInstanceId == "instance");
                await Send(writer, new BackendMessage { Kind = "event", Event = new BackendEvent
                    { Kind = "snapshot", BackendInstanceId = "instance", Snapshot = Snapshot(8, 100) } }, token);
                await Reply(writer, enumerate, new[] { new UiDevice("peer", "Enco X4", true) }, token);
                await Reply(writer, apply, new UiApplyResult { BackendReceiptReceived = true, After = Snapshot(7, 99) }, token);
                var next = await Read(reader, token);
                Check(next.Operation == "apply" && next.DeviceGeneration == 8);
                await Reply(writer, next, new UiApplyResult { BackendReceiptReceived = true }, token);
                await Drain(reader, token);
            });
            await using var bridge = new ClientBridge(pipe, testBudget: TimeSpan.FromSeconds(5));
            bridge.EventReceived += notification => { if (notification.Snapshot is { } snapshot) pushed.TrySetResult(snapshot); };
            await bridge.SubscribeAsync("main", default);
            await bridge.SelectAsync("peer", default);
            var applyTask = bridge.ApplyAsync("peer", 7, new UiAction("GameMode", 1), default);
            var enumerateTask = bridge.EnumerateAsync(default);
            await Task.WhenAll(applyTask, enumerateTask);
            Check((await pushed.Task.WaitAsync(TimeSpan.FromSeconds(3))).DeviceGeneration == 8);
            await bridge.ApplyAsync(new UiAction("GameMode", 0), default);
        });
        await test("disposing one shared client leaves the other connected and never launches a worker", async () =>
        {
            string pipe = Name();
            int started = 0, requests = 0;
            async Task Serve(RpcLineReader reader, StreamWriter writer, CancellationToken token)
            {
                while (await reader.ReadLineAsync(token) is { } line)
                {
                    var request = JsonSerializer.Deserialize<BackendRequest>(line, RpcWire.Json)!;
                    Interlocked.Increment(ref requests);
                    await Reply(writer, request, new[] { new UiDevice("peer", "Enco X4", true) }, token);
                }
            }
            await using var firstServer = new Server(pipe, Serve, 2);
            await using var secondServer = new Server(pipe, Serve, 2);
            await using var first = new ClientBridge(pipe, () => Interlocked.Increment(ref started), TimeSpan.FromSeconds(4));
            await using var second = new ClientBridge(pipe, () => Interlocked.Increment(ref started), TimeSpan.FromSeconds(4));
            await first.EnumerateAsync(default); await second.EnumerateAsync(default);
            await first.DisposeAsync();
            Check((await second.EnumerateAsync(default)).Count == 1);
            Check(started == 0 && requests == 3);
        });
        await test("shared pipe EOF after SET reports an unknown receipt without replay", async () =>
        {
            string pipe = Name(); int submitted = 0;
            await using var server = new Server(pipe, async (reader, writer, token) =>
            {
                var request = await Read(reader, token);
                Check(request.Operation == "apply");
                Interlocked.Increment(ref submitted);
                // Close without an ACK: the setter may already have reached the device.
            });
            await using var bridge = new ClientBridge(pipe, testBudget: TimeSpan.FromSeconds(4));
            var result = await bridge.ApplyAsync("peer", 7, new UiAction("HiRes", 1), default);
            Check(!result.BackendReceiptReceived && result.SubmissionAttempted && result.PartialEffects
                && result.AcknowledgementUnknown && result.FailureKind == ApplyFailure.WorkerUnavailable && submitted == 1);
        });
        await test("shared pipe rejects schema 4 after subscribing without reconnecting or restarting the backend", async () =>
        {
            string pipe = Name(); int started = 0, reconnected = 0;
            var unavailable = new TaskCompletionSource<BackendEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new Server(pipe, async (reader, writer, token) =>
            {
                var subscribe = await Read(reader, token);
                Check(subscribe.Operation == "subscribe" && subscribe.SchemaVersion == 5);
                await Reply(writer, subscribe, new BackendSubscription { BackendInstanceId = "instance" }, token);
                var request = await Read(reader, token);
                Check(request.Operation == "enumerate" && request.SchemaVersion == 5);
                await Send(writer, new BackendMessage { SchemaVersion = 4, Id = request.Id, Success = true,
                    Data = JsonSerializer.SerializeToElement(Array.Empty<UiDevice>(), RpcWire.Json) }, token);
                await Drain(reader, token);
            }, 2);
            await using var bridge = new ClientBridge(pipe, () => Interlocked.Increment(ref started), TimeSpan.FromSeconds(4));
            bridge.EventReceived += notification =>
            { if (notification.Kind == "unavailable") unavailable.TrySetResult(notification); };
            await bridge.SubscribeAsync("main", default);
            await using var reconnectServer = new Server(pipe, async (reader, writer, token) =>
            {
                Interlocked.Increment(ref reconnected);
                await Drain(reader, token);
            }, 2);
            await Throws(() => bridge.EnumerateAsync(default));
            Check((await unavailable.Task.WaitAsync(TimeSpan.FromSeconds(3))).Message?.Contains("版本不一致") == true);
            await Throws(() => bridge.EnumerateAsync(default));
            // The normal subscription reconnect delay is one second.
            await Task.Delay(1500);
            Check(started == 0 && Volatile.Read(ref reconnected) == 0);
        });
        await test("shared pipe reconnects subscriptions with a new backend instance and never replays writes", async () =>
        {
            string pipe = Name(); int writes = 0;
            var restored = new TaskCompletionSource<BackendEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var first = new Server(pipe, async (reader, writer, token) =>
            {
                var subscribe = await Read(reader, token);
                await Reply(writer, subscribe, new BackendSubscription { BackendInstanceId = "old" }, token);
                var apply = await Read(reader, token);
                Check(apply.Operation == "apply"); Interlocked.Increment(ref writes);
            }, 2);
            await using var second = new Server(pipe, async (reader, writer, token) =>
            {
                var subscribe = await Read(reader, token);
                Check(subscribe.Operation == "subscribe");
                await Reply(writer, subscribe, new BackendSubscription { BackendInstanceId = "new",
                    Devices = [new UiDevice("new-peer", "Enco X4", true)] }, token);
                await Drain(reader, token);
            }, 2);
            await using var bridge = new ClientBridge(pipe, testBudget: TimeSpan.FromSeconds(5));
            bridge.EventReceived += notification => { if (notification.BackendInstanceId == "new") restored.TrySetResult(notification); };
            await bridge.SubscribeAsync("main", default);
            var result = await bridge.ApplyAsync("peer", 7, new UiAction("GameMode", 1), default);
            Check(result.AcknowledgementUnknown);
            Check((await restored.Task.WaitAsync(TimeSpan.FromSeconds(5))).Kind == "catalog" && writes == 1);
        });
        await test("shared pipe retries a subscription that immediately closes during reconnect", async () =>
        {
            string pipe = Name();
            var transient = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var restored = new TaskCompletionSource<BackendEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var first = new Server(pipe, async (reader, writer, token) =>
            {
                var subscribe = await Read(reader, token);
                await Reply(writer, subscribe, new BackendSubscription { BackendInstanceId = "old" }, token);
                Check((await Read(reader, token)).Operation == "apply");
            }, 3);
            await using var bridge = new ClientBridge(pipe, testBudget: TimeSpan.FromSeconds(5));
            bridge.EventReceived += notification => { if (notification.BackendInstanceId == "final") restored.TrySetResult(notification); };
            await bridge.SubscribeAsync("main", default);
            await using var second = new Server(pipe, async (reader, writer, token) =>
            {
                var subscribe = await Read(reader, token);
                Check(subscribe.Operation == "subscribe");
                await Reply(writer, subscribe, new BackendSubscription { BackendInstanceId = "transient" }, token);
                transient.TrySetResult();
                // Lose this new connection before the reconnect runner settles.
            }, 3);
            Check((await bridge.ApplyAsync("peer", 7, new UiAction("GameMode", 1), default)).AcknowledgementUnknown);
            await transient.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await using var third = new Server(pipe, async (reader, writer, token) =>
            {
                var subscribe = await Read(reader, token);
                Check(subscribe.Operation == "subscribe");
                await Reply(writer, subscribe, new BackendSubscription { BackendInstanceId = "final" }, token);
                await Drain(reader, token);
            }, 3);
            Check((await restored.Task.WaitAsync(TimeSpan.FromSeconds(5))).Kind == "catalog");
        });
        await test("shared pipe shutdown vote and stopping event preserve the exit handshake", async () =>
        {
            string pipe = Name();
            var exit = new TaskCompletionSource<BackendEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new Server(pipe, async (reader, writer, token) =>
            {
                var subscribe = await Read(reader, token);
                await Reply(writer, subscribe, new BackendSubscription { BackendInstanceId = "instance" }, token);
                await Send(writer, new BackendMessage { Kind = "event", Event = new BackendEvent
                    { Kind = "requestExit", BackendInstanceId = "instance", ExitRequestId = "vote" } }, token);
                var vote = await Read(reader, token);
                Check(vote.Operation == "exitVote" && vote.ExitRequestId == "vote" && vote.Approved == true);
                await Reply(writer, vote, true, token);
                await Send(writer, new BackendMessage { Kind = "event", Event = new BackendEvent
                    { Kind = "stopping", BackendInstanceId = "instance" } }, token);
                await Drain(reader, token);
            });
            await using var bridge = new ClientBridge(pipe, testBudget: TimeSpan.FromSeconds(5));
            bridge.EventReceived += notification =>
            {
                if (notification.Kind == "requestExit") exit.TrySetResult(notification);
                if (notification.Kind == "stopping") stopping.TrySetResult();
            };
            await bridge.SubscribeAsync("main", default);
            var requestExit = await exit.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await bridge.AcknowledgeExitAsync(requestExit.ExitRequestId!, true, default);
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(3));
        });
        await test("an absent backend singleton launches the backend without the 300 ms probe and the early pipe serves the first request", async () =>
        {
            string pipe = Name(); int started = 0, requests = 0; Server? server = null;
            var launched = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            async Task Serve(RpcLineReader reader, StreamWriter writer, CancellationToken token)
            {
                while (await reader.ReadLineAsync(token) is { } line)
                {
                    var request = JsonSerializer.Deserialize<BackendRequest>(line, RpcWire.Json)!;
                    Interlocked.Increment(ref requests);
                    await Reply(writer, request, new BackendSubscription { BackendInstanceId = "instance" }, token);
                }
            }
            await using var bridge = new ClientBridge(pipe, () =>
            {
                Interlocked.Increment(ref started);
                server = new Server(pipe, Serve);
                launched.TrySetResult(clock.Elapsed);
            }, TimeSpan.FromSeconds(4), backendRunning: () => Volatile.Read(ref server) is not null);
            bridge.BeginConnect();
            Check(await launched.Task.WaitAsync(TimeSpan.FromSeconds(3)) < TimeSpan.FromMilliseconds(250));
            await bridge.SubscribeAsync("main", default);
            Check(started == 1 && requests == 1);
            await server!.DisposeAsync();
        });
        await test("disposing while an early connection waits for a launched backend returns promptly", async () =>
        {
            int started = 0;
            var bridge = new ClientBridge(Name(), () => Interlocked.Increment(ref started), TimeSpan.FromSeconds(4), backendRunning: () => false);
            bridge.BeginConnect();
            await Task.Delay(100);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await bridge.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Check(started == 1 && clock.Elapsed < TimeSpan.FromSeconds(2));
        });
        await test("an early connection failure is left to the first request, which connects without a second launch", async () =>
        {
            string pipe = Name(); int started = 0;
            await using var bridge = new ClientBridge(pipe, () =>
            { Interlocked.Increment(ref started); throw new FileNotFoundException(); }, TimeSpan.FromSeconds(4), backendRunning: () => false);
            bridge.BeginConnect();
            using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                while (Volatile.Read(ref started) == 0) await Task.Delay(10, budget.Token);
            await using var server = new Server(pipe, async (reader, writer, token) =>
            {
                var request = await Read(reader, token);
                Check(request.Operation == "enumerate");
                await Reply(writer, request, new[] { new UiDevice("peer", "Enco X4", true) }, token);
                await Drain(reader, token);
            });
            Check((await bridge.EnumerateAsync(default)).Count == 1 && started == 1);
        });
    }

    private static UiSnapshot Snapshot(long generation, long revision) => new()
    { Identity = "peer", BackendInstanceId = "instance", DeviceGeneration = generation, Revision = revision };
    private static string Name() => "HeyMelody-tests-" + Guid.NewGuid().ToString("N");
    private static async Task<BackendRequest> Read(RpcLineReader reader, CancellationToken token) =>
        JsonSerializer.Deserialize<BackendRequest>(await reader.ReadLineAsync(token) ?? throw new EndOfStreamException(), RpcWire.Json)!;
    private static Task Reply<T>(StreamWriter writer, BackendRequest request, T result, CancellationToken token) =>
        Send(writer, new BackendMessage { Id = request.Id, Success = true, Data = JsonSerializer.SerializeToElement(result, RpcWire.Json) }, token);
    private static async Task Send(StreamWriter writer, BackendMessage message, CancellationToken token)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(message, RpcWire.Json).AsMemory(), token);
        await writer.FlushAsync(token);
    }
    private static async Task Drain(RpcLineReader reader, CancellationToken token)
    { while (await reader.ReadLineAsync(token) is not null) { } }
    private static void Check(bool condition) { if (!condition) throw new Exception("Shared pipe assertion failed."); }
    private static async Task Throws(Func<Task> action)
    {
        try { await action(); }
        catch (IOException) { return; }
        throw new Exception("Expected shared pipe failure.");
    }
    private sealed class Server : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(15));
        private readonly NamedPipeServerStream _pipe;
        private readonly Task _run;
        public Server(string name, Func<RpcLineReader, StreamWriter, CancellationToken, Task> serve, int instances = 1)
        {
            _pipe = new(name, PipeDirection.InOut, instances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _run = Run(serve);
        }
        private async Task Run(Func<RpcLineReader, StreamWriter, CancellationToken, Task> serve)
        {
            try
            {
                await _pipe.WaitForConnectionAsync(_lifetime.Token);
                var reader = new RpcLineReader(_pipe, strictUtf8: true);
                using var writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, true);
                await serve(reader, writer, _lifetime.Token);
            }
            finally { _pipe.Dispose(); }
        }
        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel(); _pipe.Dispose();
            try { await _run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or IOException) { }
            _lifetime.Dispose();
        }
    }
}
