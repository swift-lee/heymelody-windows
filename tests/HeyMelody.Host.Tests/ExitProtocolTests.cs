using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;
using HeyMelodyWindows.Windows;

internal static class ExitProtocolTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        await test("exit includes manager and calibration, reaches unsubscribed peers and waits for terminal ACK or disconnect", async () =>
        {
            await using var fixture = new Fixture(scratch);
            await using var main = await fixture.Connect("main");
            await using var manager = await fixture.Connect("manager");
            await using var calibration = await fixture.Connect("calibration");
            await using var observer = await fixture.Connect("observer");
            await using var warm = await fixture.Connect();
            var exiting = fixture.Server.RequestExitAsync();
            var peers = new[] { main, manager, calibration, observer, warm };
            var prepare = await Task.WhenAll(peers.Select(peer => peer.Event("requestExit")));
            string id = prepare[0].ExitRequestId!;
            Check(prepare.All(value => value.ExitRequestId == id));
            await Task.WhenAll(new[] { main.Vote(id, true), manager.Vote(id, true), calibration.Vote(id, true) });
            var stopping = await Task.WhenAll(peers.Select(peer => peer.Event("stopping")));
            Check(stopping.All(value => value.ExitRequestId == id) && !exiting.IsCompleted && fixture.Approved == 0);
            await Task.WhenAll(new[] { main.Ack(id), manager.Ack(id), calibration.Ack(id), observer.Ack(id) });
            await warm.DisposeAsync();
            await exiting.WaitAsync(TimeSpan.FromSeconds(4));
            Check(fixture.Approved == 1);
        });

        await test("a manager subscribing during voting joins the quorum, may veto, and cancellation restores ordinary RPC", async () =>
        {
            await using var fixture = new Fixture(scratch);
            await using var main = await fixture.Connect("main");
            var exiting = fixture.Server.RequestExitAsync();
            string id = (await main.Event("requestExit")).ExitRequestId!;
            await using var manager = await fixture.Connect("manager");
            Check((await manager.Event("requestExit")).ExitRequestId == id);
            await main.Vote(id, true);
            var blocked = await main.Request("enumerate");
            Check(!blocked.Success && blocked.ErrorCode == "exitPending" && fixture.Enumerations == 0);
            await manager.Vote(id, false);
            Check((await main.Event("exitCancelled")).ExitRequestId == id);
            Check((await manager.Event("exitCancelled")).ExitRequestId == id);
            await exiting.WaitAsync(TimeSpan.FromSeconds(4));
            Check(fixture.Approved == 0 && (await main.Request("enumerate")).Success && fixture.Enumerations == 1);
            exiting = fixture.Server.RequestExitAsync();
            string retry = (await main.Event("requestExit")).ExitRequestId!;
            Check(retry != id && (await manager.Event("requestExit")).ExitRequestId == retry);
            await Task.WhenAll(new[] { main.Vote(retry, true), manager.Vote(retry, true) });
            await Task.WhenAll(new[] { main.Event("stopping"), manager.Event("stopping") });
            await Task.WhenAll(new[] { main.Ack(retry), manager.Ack(retry) });
            await exiting.WaitAsync(TimeSpan.FromSeconds(4));
        });

        await test("new connections and subscriptions during stopping receive terminal state without normal initialization", async () =>
        {
            await using var fixture = new Fixture(scratch);
            await using var observer = await fixture.Connect("observer");
            var exiting = fixture.Server.RequestExitAsync();
            string id = (await observer.Event("stopping")).ExitRequestId!;
            await using var late = await fixture.Connect();
            Check((await late.Event("stopping")).ExitRequestId == id);
            var rejected = await late.Request("subscribe", "manager");
            Check(!rejected.Success && rejected.ErrorCode == "stopping");
            rejected = await late.Request("enumerate");
            Check(!rejected.Success && rejected.ErrorCode == "stopping" && fixture.Enumerations == 0);
            try { fixture.Server.ActivateMain(false); Check(false); }
            catch (InvalidOperationException) { }
            Check(!exiting.IsCompleted);
            await Task.WhenAll(new[] { observer.Ack(id), late.Ack(id) });
            await exiting.WaitAsync(TimeSpan.FromSeconds(4));
        });

        await test("stopping acknowledgements are bound to the original backend and exit request", async () =>
        {
            await using var fixture = new Fixture(scratch);
            await using var observer = await fixture.Connect("observer");
            var exiting = fixture.Server.RequestExitAsync();
            string id = (await observer.Event("stopping")).ExitRequestId!;
            Check(!(await observer.Request("stoppingAck", exitId: Guid.NewGuid().ToString("N"))).Success);
            Check(!(await observer.Request("stoppingAck", exitId: id, backendId: "retired-backend")).Success);
            Check(!exiting.IsCompleted && fixture.Approved == 0);
            await observer.Ack(id);
            await exiting.WaitAsync(TimeSpan.FromSeconds(4));
        });

        await test("a legacy peer without a stopping ACK cannot hold the Host indefinitely", async () =>
        {
            await using var fixture = new Fixture(scratch, TimeSpan.FromMilliseconds(120));
            await using var observer = await fixture.Connect("observer");
            var elapsed = Stopwatch.StartNew();
            var exiting = fixture.Server.RequestExitAsync();
            await observer.Event("stopping");
            await exiting.WaitAsync(TimeSpan.FromSeconds(3));
            Check(fixture.Approved == 1 && elapsed.Elapsed < TimeSpan.FromSeconds(2));
            Check(await observer.Eof());
        });

        await test("final disposal drains a client accepted after the first stopping barrier", async () =>
        {
            await using var fixture = new Fixture(scratch);
            var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.BeforeDispose = async () => { cleanupEntered.SetResult(); await releaseCleanup.Task; };
            await using var observer = await fixture.Connect("observer");
            var exiting = fixture.Server.RequestExitAsync();
            string id = (await observer.Event("stopping")).ExitRequestId!;
            await observer.Ack(id); await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await using var late = await fixture.Connect();
            Check((await late.Event("stopping")).ExitRequestId == id);
            releaseCleanup.SetResult();
            Check((await late.Request("enumerate")).ErrorCode == "stopping" && !exiting.IsCompleted);
            await late.Ack(id);
            await exiting.WaitAsync(TimeSpan.FromSeconds(4));
        });
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly BackendSupervisor Backend;
        internal readonly BackendPipeServer Server;
        internal readonly string Name = "HeyMelody-Exit-" + Guid.NewGuid().ToString("N");
        internal int Approved, Enumerations;
        internal Func<Task>? BeforeDispose;
        internal Fixture(string scratch, TimeSpan? stopBudget = null)
        {
            Backend = new(new DeviceWhitelistStore(Path.Combine(scratch, Guid.NewGuid().ToString("N") + ".json")),
                _ => { Interlocked.Increment(ref Enumerations); return Task.FromResult<IReadOnlyList<BluetoothCandidate>>([]); },
                () => throw new InvalidOperationException("No device workers are allowed in exit protocol tests"));
            Server = new(Backend, Name, exitVoteBudget: TimeSpan.FromSeconds(3), stoppingBudget: stopBudget ?? TimeSpan.FromSeconds(2));
            Server.ExitApproved += async () =>
            {
                Interlocked.Increment(ref Approved);
                if (BeforeDispose is { } callback) await callback();
                await Server.DisposeAsync();
            };
            Server.Start();
        }
        internal async Task<Peer> Connect(string? role = null)
        {
            var peer = await Peer.Connect(Name, Backend.InstanceId);
            if (role is not null) Check((await peer.Request("subscribe", role)).Success);
            return peer;
        }
        public async ValueTask DisposeAsync() { await Server.DisposeAsync(); await Backend.DisposeAsync(); }
    }

    private sealed class Peer(NamedPipeClientStream pipe, string backend) : IAsyncDisposable
    {
        private readonly StreamWriter _writer = new(pipe, new UTF8Encoding(false), leaveOpen: true);
        private readonly RpcLineReader _reader = new(pipe);
        private readonly List<BackendMessage> _inbox = [];
        private long _id;
        private bool _disposed;
        internal static async Task<Peer> Connect(string name, string backend)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(3000); return new(pipe, backend);
        }
        internal async Task<BackendMessage> Request(string operation, string? role = null, string? exitId = null, string? backendId = null)
        {
            long id = await Send(new() { Operation = operation, Role = role, ExitRequestId = exitId, BackendInstanceId = backendId ?? backend });
            return await Read(message => message.Kind == "response" && message.Id == id);
        }
        internal async Task Vote(string id, bool approved)
        {
            long request = await Send(new() { Operation = "exitVote", ExitRequestId = id, Approved = approved, BackendInstanceId = backend });
            Check((await Read(message => message.Kind == "response" && message.Id == request)).Success);
        }
        internal async Task Ack(string id) => await Send(new() { Operation = "stoppingAck", ExitRequestId = id, BackendInstanceId = backend });
        private async Task<long> Send(BackendRequest request)
        {
            request.Id = ++_id;
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request, RpcWire.Json)); await _writer.FlushAsync();
            return request.Id;
        }
        internal async Task<BackendEvent> Event(string kind) => (await Read(message => message.Event?.Kind == kind)).Event!;
        private async Task<BackendMessage> Read(Func<BackendMessage, bool> match)
        {
            int index = _inbox.FindIndex(value => match(value));
            if (index >= 0) { var existing = _inbox[index]; _inbox.RemoveAt(index); return existing; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await _reader.ReadLineAsync(timeout.Token) is { } line)
            {
                var message = JsonSerializer.Deserialize<BackendMessage>(line, RpcWire.Json)!;
                if (match(message)) return message;
                _inbox.Add(message);
            }
            throw new EndOfStreamException("Exit test pipe closed before the expected message");
        }
        internal async Task<bool> Eof()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            return await _reader.ReadLineAsync(timeout.Token) is null;
        }
        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                try { _writer.Dispose(); } catch (IOException) { }
                finally { pipe.Dispose(); }
            }
            return ValueTask.CompletedTask;
        }
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Exit protocol assertion failed"); }
}
