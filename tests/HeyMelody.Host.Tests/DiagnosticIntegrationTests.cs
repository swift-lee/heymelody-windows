using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HeyMelody.Diagnostics;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;
using HeyMelodyWindows.Windows;

internal static class DiagnosticIntegrationTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        await test("diagnostic failures survive subscription and remain separate from business health", async () =>
        {
            using var logging = new Logging(Path.Combine(scratch, "diagnostic-failures"));
            var early = Failure("early");
            DiagnosticLog.ReportExternalFailure(early);
            await using var backend = new BackendSupervisor(new DeviceWhitelistStore(Path.Combine(scratch, "diagnostic-devices.json")),
                _ => Task.FromResult<IReadOnlyList<BluetoothCandidate>>([]), () => throw new InvalidOperationException("No worker expected"));
            var events = new List<BackendEvent>();
            backend.EventPublished += events.Add;
            backend.AttachDiagnostics();
            Check(backend.Subscribe().DiagnosticFailures.Single().ProcessInstanceId == early.ProcessInstanceId);
            var first = Failure("worker-one"); var second = Failure("worker-two");
            DiagnosticLog.ReportExternalFailure(first);
            DiagnosticLog.ReportExternalFailure(first);
            DiagnosticLog.ReportExternalFailure(second);
            var baseline = JsonSerializer.Deserialize<BackendSubscription>(JsonSerializer.Serialize(backend.Subscribe(), RpcWire.Json), RpcWire.Json)!;
            Check(baseline.DiagnosticFailures.Count == 3);
            Check(events.Count == 3 && events.All(value => value.Kind == "diagnosticFailure" && value.Message is null));
            DiagnosticLog.ReportExternalFailure(new()
            {
                Component = first.Component, Role = first.Role, ProcessInstanceId = first.ProcessInstanceId,
                SessionId = first.SessionId, Message = "Writer stopped after an earlier overflow warning"
            });
            Check(events.Count == 4 && backend.Subscribe().DiagnosticFailures.Count == 3
                && backend.Subscribe().DiagnosticFailures.Any(value => value.Message == "Writer stopped after an earlier overflow warning"));
            backend.DetachDiagnostics();
            DiagnosticLog.ReportExternalFailure(Failure("detached"));
            Check(backend.Subscribe().DiagnosticFailures.Count == 3);
        });

        await test("worker stderr preserves oversized native output and parses only dedicated failure lines", async () =>
        {
            string directory = Path.Combine(scratch, "diagnostic-stderr");
            using var logging = new Logging(directory);
            var failure = Failure("stderr");
            string expected = "native startup 中文\r\n" + new string('x', WorkerDiagnosticReader.MaximumFailureLineCharacters + 3)
                + "\n" + DiagnosticLog.FailurePrefix + "{broken}\n"
                + DiagnosticLog.FailurePrefix + "{\"Component\":null,\"ProcessInstanceId\":\"malformed\"}\n"
                + DiagnosticLog.FormatFailureLine(failure) + "\r\nunterminated tail";
            int received = 0;
            void Receive(DiagnosticFailure value) { if (value.ProcessInstanceId == failure.ProcessInstanceId) received++; }
            DiagnosticLog.Failed += Receive;
            try { await WorkerDiagnosticReader.DrainAsync(new StringReader(expected), "test-worker", 123); }
            finally { DiagnosticLog.Failed -= Receive; }
            DiagnosticLog.Stop("Inspecting completed stderr log");
            var actual = new StringBuilder();
            foreach (string path in Directory.GetFiles(directory, "*.jsonl").Order())
                foreach (string line in File.ReadLines(path))
                {
                    using var document = JsonDocument.Parse(line);
                    var entry = document.RootElement;
                    if (entry.GetProperty("source").GetString() == "worker.stderr" && entry.GetProperty("eventName").GetString() == "receive")
                        actual.Append(entry.GetProperty("data").GetProperty("text").GetString());
                }
            Check(received == 1 && actual.ToString() == expected);
        });

        await test("Host RPC correlation crosses the real worker boundary without submitting settings", async () =>
        {
            string directory = Path.Combine(scratch, "diagnostic-correlation");
            using var logging = new Logging(directory);
            string capture = Path.Combine(scratch, "diagnostic-worker-records.jsonl");
            var store = new DeviceWhitelistStore(Path.Combine(scratch, "diagnostic-peer.json"));
            store.Save([DeviceWhitelistStore.KeyFor(2)]);
            IReadOnlyList<BluetoothCandidate> devices = [new(2, "Synthetic headset", true, true, true, 0x404)];
            await using var backend = new BackendSupervisor(store, _ => Task.FromResult(devices),
                () => new PeerWorker(() => FakeStart(capture), TimeSpan.FromSeconds(8)));
            await backend.InitializeAsync(default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (!backend.Subscribe().Snapshots.Any(value => value.ControlReady)) await Task.Delay(10, timeout.Token);
            string name = "HeyMelody-Diagnostic-" + Guid.NewGuid().ToString("N");
            await using var server = new BackendPipeServer(backend, name);
            server.Start();
            await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var reader = new RpcLineReader(pipe);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new BackendRequest { Id = 1, Operation = "subscribe", Role = "observer" }, RpcWire.Json));
            var subscribed = await Reply(reader, 1, timeout.Token);
            var baseline = subscribed.Data!.Value.Deserialize<BackendSubscription>(RpcWire.Json)!;
            var snapshot = baseline.Snapshots.Single();
            string correlation = Guid.NewGuid().ToString("N");
            await writer.WriteLineAsync(JsonSerializer.Serialize(new BackendRequest
            {
                Id = 2, Operation = "refresh", BackendInstanceId = baseline.BackendInstanceId,
                Identity = snapshot.Identity, DeviceGeneration = snapshot.DeviceGeneration,
                RefreshScope = RefreshScope.Full, DiagnosticCorrelationId = correlation
            }, RpcWire.Json));
            Check((await Reply(reader, 2, timeout.Token)).Success);
            var requests = File.ReadLines(capture).Select(line => JsonSerializer.Deserialize<WorkerRequest>(line, HostWire.Json)!).ToArray();
            Check(requests.Any(value => value.Operation == "refresh" && value.DiagnosticCorrelationId == correlation));
            Check(requests.All(value => value.Operation != "apply"));
            long refreshId = requests.Single(value => value.Operation == "refresh").Id;
            await backend.StopGracefullyAsync();
            DiagnosticLog.Stop("Inspecting worker RX correlation records");
            bool correlatedReply = false;
            foreach (string path in Directory.GetFiles(directory, "*.jsonl"))
                foreach (string line in File.ReadLines(path))
                {
                    using var document = JsonDocument.Parse(line);
                    var entry = document.RootElement;
                    if (entry.GetProperty("source").GetString() != "host.worker" || entry.GetProperty("eventName").GetString() != "receive") continue;
                    var message = JsonSerializer.Deserialize<WorkerMessage>(entry.GetProperty("data").GetProperty("line").GetString()!, HostWire.Json)!;
                    if (message.Kind == "response" && message.Id == refreshId)
                        correlatedReply = entry.GetProperty("correlationId").GetString() == correlation;
                }
            Check(correlatedReply);
        });
    }

    internal static async Task<int> FakePeerMainAsync(string capture)
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        string identity = "";
        while (await Console.In.ReadLineAsync() is { } line)
        {
            var request = JsonSerializer.Deserialize<WorkerRequest>(line, HostWire.Json)!;
            await File.AppendAllTextAsync(capture, line + Environment.NewLine);
            if (request.Operation == "initialize") identity = request.Identity;
            object data = request.Operation == "shutdown" ? true : new UiSnapshot
            {
                Identity = identity, DeviceName = "Synthetic headset", ProductId = "06EC10", SupportedModel = true,
                ControlReady = true, Connected = true, ConnectionState = ConnectionState.Connected, Revision = request.Id
            };
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new WorkerMessage
                { Id = request.Id, Success = true, Data = JsonSerializer.SerializeToElement(data, HostWire.Json) }, HostWire.Json));
            await Console.Out.FlushAsync();
            if (request.Operation == "shutdown") break;
        }
        return 0;
    }

    private static ProcessStartInfo FakeStart(string capture)
    {
        string path = Environment.ProcessPath!;
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--diagnostic-fake-peer"); start.ArgumentList.Add(capture);
        return start;
    }

    private static async Task<BackendMessage> Reply(RpcLineReader reader, long id, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token) is { } line)
        {
            var message = JsonSerializer.Deserialize<BackendMessage>(line, RpcWire.Json)!;
            if (message.Kind == "response" && message.Id == id) return message;
        }
        throw new EndOfStreamException();
    }

    private static DiagnosticFailure Failure(string role) => new()
    {
        Component = DiagnosticComponents.Host, Role = role, ProcessInstanceId = Guid.NewGuid().ToString("N"),
        SessionId = DiagnosticLog.SessionId, Message = "Synthetic diagnostic writer failure"
    };
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Diagnostic integration assertion failed"); }

    private sealed class Logging : IDisposable
    {
        internal Logging(string directory)
        {
            Directory.CreateDirectory(directory);
            DiagnosticLog.InitializeIdentity(DiagnosticComponents.Host, "test");
            DiagnosticLog.Configure(new() { Enabled = true, SessionId = Guid.NewGuid().ToString("N"),
                Directory = directory, Components = [DiagnosticComponents.Host] });
            Check(DiagnosticLog.IsEnabled);
        }
        public void Dispose() => DiagnosticLog.Stop("Host integration test finished");
    }
}
