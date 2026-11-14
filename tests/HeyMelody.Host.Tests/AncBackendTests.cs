using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Control;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Host;
using HeyMelodyWindows.Protocol;

internal static class AncBackendTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        await test("schema 5 ANC additions default safely and retain legacy packed actions", () =>
        {
            var legacy = JsonSerializer.Deserialize<UiSnapshot>("{\"anc\":2829,\"canSetAnc\":true}", RpcWire.Json)!;
            Check(legacy is { Anc: 0x0B0D, CanSetAnc: true, AncPrimary: null, CanSetAncMode: false },
                "An older snapshot implied new parent state or write capability");
            var message = new BackendMessage
            {
                Data = JsonSerializer.SerializeToElement(new UiSnapshot { AncPrimary = 11, CanSetAncMode = true }, RpcWire.Json)
            };
            var roundtrip = JsonSerializer.Deserialize<BackendMessage>(JsonSerializer.Serialize(message, RpcWire.Json), RpcWire.Json)!;
            Check(roundtrip.SchemaVersion == 5
                && roundtrip.Data!.Value.Deserialize<UiSnapshot>(RpcWire.Json) is { AncPrimary: 11, Anc: null, CanSetAncMode: true },
                "New parent state did not roundtrip in the existing schema");
            foreach (var action in new[] { new UiAction("AncMode", 11), new UiAction("Anc", 0x0B0D) })
            {
                var request = new BackendRequest { Action = JsonSerializer.SerializeToElement(action, RpcWire.Json) };
                var decoded = JsonSerializer.Deserialize<BackendRequest>(JsonSerializer.Serialize(request, RpcWire.Json), RpcWire.Json)!;
                Check(decoded.SchemaVersion == 5 && decoded.Action.Deserialize<UiAction>(RpcWire.Json) == action,
                    "The new action changed the schema or reinterpreted a legacy adjustment");
            }
            return Task.CompletedTask;
        });
        await test("native action routing separates parent selection from legacy adjustment without Bluetooth", async () =>
        {
            var connector = new AncConnector();
            await using var service = new NativeHeadsetService(
                () => throw new InvalidOperationException("ANC routing enumerated Bluetooth"),
                new DeviceWhitelistStore(Path.Combine(scratch, "anc-routing.json")));
            var client = new DailyClient(connector);
            typeof(NativeHeadsetService).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, client);
            var parent = await service.ApplyAsync(new("AncMode", 11), default);
            Check(parent is { Accepted: true, ReadbackMatches: true, TargetSubmitted: true }
                && parent.After is { AncPrimary: 11, Anc: null }, "Parent action required a known adjustment to verify");
            Check(connector.Writes is [{ Command: 0x0404 }]
                && Convert.ToHexString(connector.Writes[0].Payload) == "01010008", "Parent action selected an adjustment instead");
            var adjustment = await service.ApplyAsync(new("Anc", 0x0B0E), default);
            Check(adjustment is { Accepted: true, ReadbackMatches: true }
                && adjustment.After is { AncPrimary: 11, Anc: 0x0B0E }
                && connector.Writes.Count == 2 && Convert.ToHexString(connector.Writes[1].Payload) == "01010040",
                "Legacy packed action no longer selected its exact adjustment");
            int requests = connector.Requests;
            foreach (int invalid in new[] { -1, 256, 0x0B0D, int.MaxValue })
            {
                var rejected = await service.ApplyAsync(new("AncMode", invalid), default);
                Check(rejected is { FailureKind: ApplyFailure.Validation, TargetSubmitted: false, SubmissionAttempted: false },
                    "Malformed parent ID was truncated or submitted");
            }
            Check(connector.Requests == requests, "Malformed parent requests reached the transport");
            var childAsParent = await service.ApplyAsync(new("AncMode", 13), default);
            Check(childAsParent is { FailureKind: ApplyFailure.Validation, TargetSubmitted: false }
                && connector.Writes.Count == 2, "A child protocol ID was accepted as a parent");
        });
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

    private sealed class AncConnector : IControlConnector
    {
        internal List<Packet> Writes { get; } = [];
        internal int Requests;
        private byte[] _ancReply = [0, 1, 1, 8];
        public Task<IControlTransport> OpenAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult<IControlTransport>(new Transport(this)); }
        private byte[] Reply(Packet request)
        {
            Requests++;
            if (request.Command == 0x0404)
            {
                Writes.Add(request);
                // Echo the independently asserted payload so a parent readback has no invented child.
                _ancReply = [0, .. request.Payload];
                return [0];
            }
            return request.Command switch
            {
                0x0103 => [0, 0x10, 0xEC, 6],
                0x010B => [1], // This fixture declines the optional color read.
                0x0100 => [0, 0, 1], // Only capability bit 8: ANC GET and SET.
                0x010D => [0, 0],
                0x0106 => [0, 1, 1, 75],
                0x010C => _ancReply,
                _ => throw new InvalidOperationException($"Unexpected memory transport command {request.Command:X4}")
            };
        }
        private sealed class Transport(AncConnector owner) : IControlTransport
        {
            private readonly Channel<byte[]> _received = Channel.CreateUnbounded<byte[]>();
            public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var request = new PacketDecoder().Feed(bytes.Span).Single();
                _received.Writer.TryWrite(PacketEncoder.Encode((ushort)(request.Command | 0x8000), request.Sequence, owner.Reply(request)));
                return Task.CompletedTask;
            }
            public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
            {
                if (!await _received.Reader.WaitToReadAsync(token)) return 0;
                var bytes = await _received.Reader.ReadAsync(token); bytes.CopyTo(buffer); return bytes.Length;
            }
            public ValueTask DisposeAsync() { _received.Writer.TryComplete(); return ValueTask.CompletedTask; }
        }
    }
}
