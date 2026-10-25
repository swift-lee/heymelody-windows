using System.Text.Json;
using HeyMelody.Diagnostics;

/// <summary>Redaction, never-throw capture, writer replacement and swallowed-exception visibility.</summary>
internal static class CaptureChecks
{
    internal static void Run(string root, Action<bool, string> check)
    {
        DiagnosticSessionInfo Session(string name) => new()
        {
            Enabled = true, SessionId = Guid.NewGuid().ToString("N"), Directory = Path.Combine(root, name),
            Components = [DiagnosticComponents.Main]
        };
        string ReadText(string directory) => string.Concat(Directory.GetFiles(directory, "*.jsonl").Order().Select(File.ReadAllText));
        List<JsonElement> Read(string directory) => Directory.GetFiles(directory, "*.jsonl").Order().SelectMany(File.ReadAllLines)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();
        JsonElement Event(List<JsonElement> rows, string name) => rows.Single(row => row.GetProperty("eventName").GetString() == name);

        DiagnosticLog.ResetForTests();
        DiagnosticLog.InitializeIdentity(DiagnosticComponents.Main, "main");
        var session = Session("redaction");
        DiagnosticLog.Configure(session);
        const string key = "fixture-cloud-key-0001", signature = "fixture-signature-0002";
        var rawLine = "{\"id\":7,\"settings\":{\"heyMelodyKey\":\"" + key + "\",\"useOnlineResources\":true}}";
        DiagnosticLog.Debug("Fixture", "redaction", "credentials", new
        {
            request = new { Settings = new { HeyMelodyKey = key, UseOnlineResources = true } },
            line = rawLine, ordinary = "{\"command\":\"0x0114\"}",
            headers = new Dictionary<string, string> { ["sign"] = signature, ["ts"] = "1700000000000" },
            emptyKey = new { heyMelodyKey = "" }
        });
        DiagnosticLog.Stop("redaction completed");
        var text = ReadText(session.Directory!);
        var data = Event(Read(session.Directory!), "redaction").GetProperty("data");
        check(!text.Contains(key) && !text.Contains(signature), "cloud key and request signature never reach the log file");
        var described = data.GetProperty("request").GetProperty("Settings").GetProperty("HeyMelodyKey");
        check(described.GetProperty("redacted").GetBoolean() && described.GetProperty("length").GetInt32() == key.Length
            && described.GetProperty("sha256Prefix").GetString()!.Length == 12, "redacted key keeps length and hash prefix for comparison");
        var line = JsonDocument.Parse(data.GetProperty("line").GetString()!).RootElement;
        check(line.GetProperty("id").GetInt32() == 7 && line.GetProperty("settings").GetProperty("heyMelodyKey").GetProperty("redacted").GetBoolean()
            && line.GetProperty("settings").GetProperty("useOnlineResources").GetBoolean(), "embedded raw IPC line is redacted and otherwise preserved");
        check(data.GetProperty("ordinary").GetString() == "{\"command\":\"0x0114\"}"
            && data.GetProperty("headers").GetProperty("ts").GetString() == "1700000000000", "records without credentials stay byte-for-byte original");
        check(data.GetProperty("emptyKey").GetProperty("heyMelodyKey").GetProperty("length").GetInt32() == 0, "empty key reports unset without a hash");

        DiagnosticLog.ResetForTests();
        DiagnosticLog.InitializeIdentity(DiagnosticComponents.Main, "main");
        session = Session("never-throw");
        DiagnosticLog.Configure(session);
        bool threw = false;
        try
        {
            DiagnosticLog.Debug("Fixture", "throwingGetter", "getter", new ThrowingGetter());
            DiagnosticLog.Error("Fixture", "throwingException", "exception", new ThrowingToString());
            DiagnosticLog.Bytes("Fixture", "emptyBytes", ReadOnlySpan<byte>.Empty, new ThrowingGetter());
        }
        catch (Exception) { threw = true; }
        try { throw new FormatException("swallowed-fixture"); } catch (FormatException) { }
        DiagnosticLog.Stop("never-throw completed");
        var rows = Read(session.Directory!);
        check(!threw && Event(rows, "throwingGetter").GetProperty("data").GetProperty("serializationError").GetString()!.Contains("getter-fixture"),
            "a throwing diagnostic getter is recorded without failing the caller");
        check(Event(rows, "throwingException").GetProperty("exception").GetString()!.Contains(typeof(ThrowingToString).FullName!),
            "an exception whose ToString fails is still identified");
        check(rows.Any(row => row.GetProperty("eventName").GetString() == "exception.firstChance"
            && row.GetProperty("exception").GetString()!.Contains("swallowed-fixture")), "swallowed exceptions are visible as first-chance records");

        DiagnosticLog.ResetForTests();
        DiagnosticLog.InitializeIdentity(DiagnosticComponents.Main, "main");
        var first = Session("replace-first");
        var second = Session("replace-second");
        DiagnosticLog.Configure(first);
        DiagnosticLog.Configure(second);
        DiagnosticLog.Debug("Fixture", "afterReplace", "second writer only");
        DiagnosticLog.Stop("replace completed");
        check(!Read(first.Directory!).Any(row => row.GetProperty("eventName").GetString() == "afterReplace")
            && Read(second.Directory!).Any(row => row.GetProperty("eventName").GetString() == "afterReplace"),
            "a repeated configure closes the earlier writer instead of writing to both");
        DiagnosticLog.ResetForTests();
    }

    private sealed class ThrowingGetter
    {
        public string Value => throw new InvalidOperationException("getter-fixture");
    }

    private sealed class ThrowingToString : Exception
    {
        public override string ToString() => throw new InvalidOperationException("tostring-fixture");
    }
}
