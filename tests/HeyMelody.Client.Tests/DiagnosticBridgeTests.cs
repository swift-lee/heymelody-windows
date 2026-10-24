using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HeyMelody.Diagnostics;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;

internal static class DiagnosticBridgeTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        await test("disabled diagnostic logging does not add correlation metadata to RPC", async () =>
        {
            DiagnosticLog.Stop("disabled RPC test");
            await Exchange(async (reader, writer, token) =>
            {
                string line = (await reader.ReadLineAsync(token))!;
                using var document = JsonDocument.Parse(line);
                Check(!document.RootElement.TryGetProperty("diagnosticCorrelationId", out _));
                var request = JsonSerializer.Deserialize<BackendRequest>(line, RpcWire.Json)!;
                await Reply(writer, request.Id, Array.Empty<UiDevice>(), token);
            }, async bridge => Check((await bridge.EnumerateAsync(default)).Count == 0));
        });

        await test("concurrent logged RPCs have distinct correlation IDs and retain numeric response matching", async () =>
        {
            string directory = Path.Combine(scratch, "diagnostic-rpc"); Directory.CreateDirectory(directory);
            DiagnosticLog.InitializeIdentity(DiagnosticComponents.Main, "client-test");
            DiagnosticLog.Configure(new() { Enabled = true, SessionId = Guid.NewGuid().ToString("N"),
                Components = [DiagnosticComponents.Main], Directory = directory });
            try
            {
                Check(DiagnosticLog.IsEnabled);
                var correlations = new Dictionary<long, string?>();
                await Exchange(async (reader, writer, token) =>
                {
                    var first = await Read(reader, token); var second = await Read(reader, token);
                    Check(Guid.TryParse(first.DiagnosticCorrelationId, out _) && Guid.TryParse(second.DiagnosticCorrelationId, out _));
                    Check(first.DiagnosticCorrelationId != second.DiagnosticCorrelationId && first.Id != second.Id);
                    correlations[first.Id] = first.DiagnosticCorrelationId; correlations[second.Id] = second.DiagnosticCorrelationId;
                    await Send(writer, new() { Kind = "event", Event = new() { Kind = "health", BackendInstanceId = "test", Message = "Synthetic notification" } }, token);
                    await Reply(writer, second.Id, new[] { new UiDevice("second", "second", false) }, token);
                    await Reply(writer, first.Id, new[] { new UiDevice("first", "first", false) }, token);
                }, async bridge =>
                {
                    var first = bridge.EnumerateAsync(default); var second = bridge.EnumerateAsync(default);
                    Check((await first).Single().Identity == "first");
                    Check((await second).Single().Identity == "second");
                });
                DiagnosticLog.Stop("Inspecting RPC correlation records");
                int responses = 0, notifications = 0;
                foreach (string path in Directory.GetFiles(directory, "*.jsonl"))
                    foreach (string line in File.ReadLines(path))
                    {
                        using var document = JsonDocument.Parse(line);
                        var entry = document.RootElement;
                        if (entry.GetProperty("source").GetString() != "client.ipc" || entry.GetProperty("eventName").GetString() != "receive") continue;
                        var message = JsonSerializer.Deserialize<BackendMessage>(entry.GetProperty("data").GetProperty("record").GetString()!, RpcWire.Json)!;
                        string? correlation = entry.GetProperty("correlationId").GetString();
                        if (message.Kind == "response") { Check(correlation == correlations[message.Id]); responses++; }
                        else { Check(correlation is null); notifications++; }
                    }
                Check(responses == 2 && notifications == 1);
            }
            finally { DiagnosticLog.Stop("logged RPC test finished"); }
        });

        await test("baseline and multiple runtime diagnostic failures reach local warning state without replacing device status", async () =>
        {
            var failures = Enumerable.Range(0, 3).Select(index => new DiagnosticFailure
            {
                Component = DiagnosticComponents.Host, Role = "synthetic-" + index,
                ProcessInstanceId = Guid.NewGuid().ToString("N"), Message = "Synthetic log writer failure"
            }).ToArray();
            var seen = new ConcurrentDictionary<string, byte>();
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Failed(DiagnosticFailure failure)
            {
                if (failures.Any(value => value.ProcessInstanceId == failure.ProcessInstanceId)) seen.TryAdd(failure.ProcessInstanceId, 0);
                if (seen.Count == 3) received.TrySetResult();
            }
            DiagnosticLog.Failed += Failed;
            try
            {
                await Exchange(async (reader, writer, token) =>
                {
                    var request = await Read(reader, token);
                    await Reply(writer, request.Id, new BackendSubscription
                        { BackendInstanceId = "diagnostic-backend", DiagnosticFailures = [failures[0]] }, token);
                    foreach (var failure in failures.Skip(1))
                        await Send(writer, new() { Kind = "event", Event = new() { Kind = "diagnosticFailure",
                            BackendInstanceId = "diagnostic-backend", DiagnosticFailure = failure } }, token);
                    await release.Task.WaitAsync(token);
                }, async bridge =>
                {
                    await using var vm = new MainViewModel(bridge, new Confirmations());
                    bridge.EventReceived += vm.AdoptBackendEvent;
                    string status = vm.Status; bool warning = vm.StatusWarning;
                    await bridge.SubscribeAsync("main", default);
                    await received.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    Check(vm.Status == status && vm.StatusWarning == warning && seen.Count == 3);
                    release.TrySetResult();
                });
            }
            finally { DiagnosticLog.Failed -= Failed; release.TrySetResult(); }
        });
    }

    private static async Task Exchange(Func<RpcLineReader, StreamWriter, CancellationToken, Task> serve, Func<ClientBridge, Task> use)
    {
        string name = "HeyMelody-DiagnosticClient-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var server = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
            await serve(new RpcLineReader(pipe), writer, timeout.Token);
        });
        await using var bridge = new ClientBridge(name, testBudget: TimeSpan.FromSeconds(5));
        try { await use(bridge); await server; }
        finally { timeout.Cancel(); }
    }

    private static async Task<BackendRequest> Read(RpcLineReader reader, CancellationToken token)
        => JsonSerializer.Deserialize<BackendRequest>((await reader.ReadLineAsync(token))!, RpcWire.Json)!;
    private static Task Reply(StreamWriter writer, long id, object data, CancellationToken token)
        => Send(writer, new() { Id = id, Success = true, Data = JsonSerializer.SerializeToElement(data, RpcWire.Json) }, token);
    private static async Task Send(StreamWriter writer, BackendMessage message, CancellationToken token)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(message, RpcWire.Json).AsMemory(), token);
        await writer.FlushAsync(token);
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Diagnostic client assertion failed"); }
    private sealed class Confirmations : IConfirmationService
    { public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken) => Task.FromResult(false); }
}
