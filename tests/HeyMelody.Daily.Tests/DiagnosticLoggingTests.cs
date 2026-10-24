using System.Text.Json;
using HeyMelody.Diagnostics;
using HeyMelodyWindows.Control;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Resources;
using System.Net;
using System.Text;

internal static class DiagnosticLoggingTests
{
    internal static async Task<int> RunAsync()
    {
        string folder = Path.Combine(Path.GetTempPath(), "HeyMelody-DailyLogging-" + Guid.NewGuid().ToString("N"));
        DiagnosticLog.InitializeIdentity(DiagnosticComponents.Host, "worker");
        DiagnosticLog.Configure(new DiagnosticSessionInfo { Enabled = true, SessionId = Guid.NewGuid().ToString("N"),
            Components = [DiagnosticComponents.Host], Directory = folder });
        byte[]? compatibilityWire = null;
        string compatibilityCorrelation = Guid.NewGuid().ToString("N");
        string combinedCorrelation = Guid.NewGuid().ToString("N");
        const string invalidMetadata = "{broken resource metadata";
        var http = new InvalidMetadataHandler(invalidMetadata);
        var accepted = new Fake(); var combined = new Fake(); var malformed = new Fake(); var fields = new Fake(); var eof = new Fake();
        try
        {
            using (DiagnosticLog.BeginOperation(compatibilityCorrelation))
            {
                await using var client = new DailyClient(new CodecWireFixtureConnector(accepted, sequence =>
                { compatibilityWire = CodecWireFixtureConnector.Air4ProV125Ack(sequence); return compatibilityWire; }, chunkSize: 3));
                var snapshot = await client.RefreshAsync();
                Require(snapshot.Connected && snapshot.CodecRaw == 8, "The logging fixture must preserve the accepted codec result.");
            }
            using (DiagnosticLog.BeginOperation(combinedCorrelation))
            {
                await using var client = new DailyClient(new CodecWireFixtureConnector(combined, sequence =>
                    [.. CodecWireFixtureConnector.Ack(sequence), .. CodecWireFixtureConnector.PresetNotification(240)]));
                Require((await client.RefreshAsync()).Connected, "A codec reply and independent notification in one chunk must remain accepted.");
            }
            using (DiagnosticLog.BeginOperation("daily-codec-malformed"))
            {
                await using var client = new DailyClient(new CodecWireFixtureConnector(malformed,
                    sequence => CodecWireFixtureConnector.Air4ProV125Ack(sequence, 0xFE), chunkSize: 2));
                var snapshot = await client.RefreshAsync();
                Require(snapshot.Connected && snapshot.Warnings.Any(message => message.Contains("已跳过")) && malformed.Opens == 2,
                    "The malformed codec fixture must retain its existing one-fresh-session recovery.");
            }
            using (DiagnosticLog.BeginOperation("daily-codec-fields"))
            {
                fields.ReadReplies[0x0114] = [0];
                await using var client = new DailyClient(fields);
                var snapshot = await client.RefreshAsync();
                Require(snapshot.Connected && snapshot.CodecRaw is null && snapshot.Warnings.Any(message => message.Contains("布局")),
                    "A valid frame with missing codec fields must remain unknown.");
            }
            using (DiagnosticLog.BeginOperation("daily-codec-eof"))
            {
                await using var client = new DailyClient(new CodecEofConnector(eof));
                Require(!(await client.RefreshAsync()).Connected, "An incomplete response followed by EOF must remain a failed connection.");
            }
            using (var client = new HttpClient(http))
            using (var vendor = new VendorResourceClient(client))
            {
                bool failed = false;
                try { await vendor.GetConfigurationMetadataAsync("synthetic-key", default); }
                catch (JsonException) { failed = true; }
                Require(failed && http.Requests == 1, "The resource fixture must fail through its local handler without another request.");
            }
        }
        finally { DiagnosticLog.Stop("Daily diagnostic fixtures completed"); }
        var records = Directory.GetFiles(folder, "*.jsonl", SearchOption.AllDirectories)
            .SelectMany(File.ReadLines).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        int passed = 0;
        var codecChunks = records.Where(record => Event(record) == "transport.rx")
            .Where(record => Context(record).TryGetProperty("pendingCorrelationId", out var id) && id.GetString() == compatibilityCorrelation)
            .Where(record => Context(record).GetProperty("pendingCommand").GetString() == "0x0114").ToArray();
        Require(codecChunks.Length > 1 && codecChunks.All(record => record.GetProperty("correlationId").ValueKind == JsonValueKind.Null)
            && string.Concat(codecChunks.Select(record => record.GetProperty("data").GetProperty("hex").GetString()))
            == Convert.ToHexString(compatibilityWire!), "The pre-decode RX records must reconstruct the complete independent codec wire fixture.");
        Pass("split raw codec reply is complete and linked to its pending operation", ref passed);
        Require(records.Any(record => Event(record) == "response.matched" && record.GetProperty("correlationId").GetString() == compatibilityCorrelation
            && Context(record).GetProperty("command").GetString() == "0x8114"), "Only the proven matched packet should inherit the request operation.");
        Pass("matched codec reply uses the request correlation", ref passed);
        Require(records.Any(record => Event(record) == "packet.unmatched" && Context(record).GetProperty("command").GetString() == "0x0504"
            && record.GetProperty("correlationId").ValueKind == JsonValueKind.Null),
            "An independent notification in the same raw chunk must not inherit the pending request correlation.");
        Pass("same-chunk unsolicited packet is not falsely associated", ref passed);
        Require(records.Any(record => Event(record) == "codec.recovery-planned"
            && record.GetProperty("exception").GetString()!.Contains("0x0114", StringComparison.Ordinal)),
            "The codec recovery decision must retain the failing command and exception.");
        Pass("codec recovery decision retains full exception", ref passed);
        Require(records.Any(record => Event(record) == "query.invalid-fields"
            && record.GetProperty("data").GetProperty("command").GetString() == "0x0114"
            && record.GetProperty("data").GetProperty("payloadLength").GetInt32() == 1
            && record.GetProperty("exception").GetString()!.Contains("Parse", StringComparison.Ordinal)),
            "Business-field failure must remain distinct from frame failure with its actual payload length.");
        Pass("codec field parser failure has payload length and stack", ref passed);
        Require(records.Any(record => Event(record) == "transport.incomplete-eof"
            && record.GetProperty("exception").GetString()!.Contains("Complete", StringComparison.Ordinal)),
            "EOF must retain the decoder's incomplete-frame failure before reporting disconnect.");
        Pass("partial EOF retains the decoder exception", ref passed);
        Require(new[] { accepted, combined, malformed, fields, eof }.All(fake => fake.Writes.Count == 0)
            && malformed.Requests.Count(packet => packet.Command == 0x0114) == 1,
            "Logging must not add SETs or replay the failed codec query.");
        Pass("logging does not introduce writes or codec retries", ref passed);
        Require(records.Any(record => Event(record) == "http.metadata-body"
            && record.GetProperty("data").GetProperty("hex").GetString() == Convert.ToHexString(Encoding.UTF8.GetBytes(invalidMetadata))),
            "A metadata parse failure must preserve its complete received body.");
        Pass("resource metadata body survives a parse failure", ref passed);
        Require(records.Any(record => Event(record) == "http.metadata-invalid"
            && record.GetProperty("exception").GetString()!.Contains("PostAsync", StringComparison.Ordinal)),
            "The metadata failure must retain its original parser exception stack.");
        Pass("resource parser failure retains full exception", ref passed);
        Console.WriteLine("Diagnostic evidence: " + folder);
        return passed;
    }
    private static string? Event(JsonElement record) => record.GetProperty("eventName").GetString();
    private static JsonElement Context(JsonElement record) => record.GetProperty("data").GetProperty("context");
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pass(string message, ref int passed) { passed++; Console.WriteLine("PASS " + message); }

    private sealed class CodecEofConnector(Fake inner) : IControlConnector
    {
        public async Task<IControlTransport> OpenAsync(CancellationToken token) => new Transport(await inner.OpenAsync(token));
        private sealed class Transport(IControlTransport inner) : IControlTransport
        {
            private bool _eof;
            public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) => inner.SendAsync(bytes, token);
            public async Task<int> ReadAsync(Memory<byte> bytes, CancellationToken token)
            {
                if (_eof) return 0;
                int count = await inner.ReadAsync(bytes, token);
                if (count >= 9 && bytes.Span[4] == 0x14 && bytes.Span[5] == 0x81) { _eof = true; return count - 1; }
                return count;
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
    private sealed class InvalidMetadataHandler(string body) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
