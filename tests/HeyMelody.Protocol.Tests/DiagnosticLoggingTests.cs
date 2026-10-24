using System.Text.Json;
using HeyMelody.Diagnostics;
using HeyMelodyWindows.Protocol;

internal static class DiagnosticLoggingTests
{
    internal static int Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "HeyMelody-ProtocolLogging-" + Guid.NewGuid().ToString("N"));
        DiagnosticLog.InitializeIdentity(DiagnosticComponents.Host, "worker");
        DiagnosticLog.Configure(new DiagnosticSessionInfo { Enabled = true, SessionId = Guid.NewGuid().ToString("N"),
            Components = [DiagnosticComponents.Host], Directory = folder });
        int passed = 0;
        try
        {
            using (DiagnosticLog.BeginOperation("protocol-fixture"))
            {
                byte[] frame = PacketEncoder.Encode(0x8114, 19, [0, 8]);
                var decoder = new PacketDecoder();
                decoder.Feed(frame.AsSpan(0, 3)); decoder.Feed(frame.AsSpan(3));
                // The writer is asynchronous: reusing the caller's input must not corrupt its bytes.
                frame.AsSpan().Clear();
                byte[] malformed = Convert.FromHexString("AA0A00001481130200000800");
                ExpectFormat(() => new PacketDecoder().Feed(malformed));
                var partial = new PacketDecoder(); partial.Feed([0xAA, 9, 0]);
                ExpectFormat(partial.Complete);
                Require(!partial.HasIncompleteMessage, "EOF must retain the original decoder reset behavior.");
                var compatible = new PacketDecoder(_ => true);
                Require(compatible.Feed(malformed).Single().Payload.SequenceEqual(new byte[] { 0, 8, 0 }), "The verified compatibility fixture must remain accepted.");
            }
        }
        finally { DiagnosticLog.Stop("Protocol diagnostic fixture completed"); }
        var records = Directory.GetFiles(folder, "*.jsonl", SearchOption.AllDirectories)
            .SelectMany(File.ReadLines).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        var input = records.Where(record => Event(record) == "decode.input").ToArray();
        byte[] expected = PacketEncoder.Encode(0x8114, 19, [0, 8]);
        Require(input[0].GetProperty("data").GetProperty("hex").GetString() == Convert.ToHexString(expected.AsSpan(0, 3))
            && input[1].GetProperty("data").GetProperty("hex").GetString() == Convert.ToHexString(expected.AsSpan(3)),
            "Raw RX chunks must be copied before the caller reuses its buffer.");
        Pass("raw input copied without alteration", ref passed);
        var failure = records.Single(record => Event(record) == "decode.failed");
        var details = failure.GetProperty("data");
        Require(details.GetProperty("failureReason").GetString() == "inner-length"
            && details.GetProperty("declaredMessagePayloadLength").GetInt32() == 2
            && details.GetProperty("actualMessagePayloadLength").GetInt32() == 3
            && details.GetProperty("responseCommand").GetInt32() == 0x8114
            && details.GetProperty("responseSequence").GetInt32() == 19
            && failure.GetProperty("exception").GetString()!.Contains("ParseInner", StringComparison.Ordinal),
            "Malformed replies must retain length, request correlation and the parser stack.");
        Pass("malformed codec response context", ref passed);
        Require(records.Any(record => Event(record) == "decode.buffered-frame"
            && record.GetProperty("data").GetProperty("hex").GetString() == "AA0A00001481130200000800"),
            "The full malformed frame must survive decoder reset.");
        Pass("malformed frame survives reset in the log", ref passed);
        Require(records.Any(record => Event(record) == "decode.incomplete-eof"
            && record.GetProperty("data").GetProperty("bufferedFrameLength").GetInt32() == 3),
            "A partial EOF must retain the incomplete frame context.");
        Pass("partial EOF context", ref passed);
        var compatibility = records.Single(record => Event(record) == "codec.compatibility-used").GetProperty("data");
        Require(compatibility.GetProperty("declaredLength").GetInt32() == 2
            && compatibility.GetProperty("actualPayloadLength").GetInt32() == 3,
            "The accepted compatibility exception must be explicitly recorded.");
        Pass("accepted compatibility layout", ref passed);
        Require(!DiagnosticLog.IsEnabled, "Stopping the fixture must leave logging disabled.");
        ExpectFormat(() => new PacketDecoder().Feed([0]));
        Pass("disabled logging preserves strict rejection", ref passed);
        Console.WriteLine("Diagnostic evidence: " + folder);
        return passed;
    }
    private static string? Event(JsonElement record) => record.GetProperty("eventName").GetString();
    private static void ExpectFormat(Action action)
    { try { action(); } catch (FormatException) { return; } throw new Exception("Expected the protocol fixture to fail."); }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pass(string message, ref int passed) { passed++; Console.WriteLine("PASS " + message); }
}
