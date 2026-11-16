using System.Text.Json;
using System.Text.Json.Nodes;
using HeyMelody.Diagnostics;

internal static class ExplicitCaptureChecks
{
    internal static void Run(string root, Action<bool, string> check)
    {
        DiagnosticLog.ResetForTests();
        DiagnosticLog.InitializeIdentity(DiagnosticComponents.Main, "main");
        var directory = Path.Combine(root, "explicit-json");
        var session = new DiagnosticSessionInfo
        {
            Enabled = true, SessionId = Guid.NewGuid().ToString("N"), Directory = directory,
            Components = [DiagnosticComponents.Main]
        };
        DiagnosticLog.Configure(session);
        var nested = new JsonObject { ["state"] = "before" };
        var payload = DiagnosticData.Object(("nested", nested), ("bytes", new byte[] { 1, 2, 3 }),
            ("values", new[] { 4, 5 }), ("uri", new Uri("https://example.invalid/resource")),
            ("unsignedEnum", LargeValue.Maximum), ("nullValue", null));
        DiagnosticLog.Info("Fixture", "explicit.capture", "Explicit diagnostic fields", payload);
        nested["state"] = "after";
        payload["values"] = new JsonArray(99);
        DiagnosticLog.Bytes("Fixture", "explicit.bytes", [1, 2], DiagnosticData.Object(("operation", "read")));
        DiagnosticLog.Stop("explicit capture completed");
        var rows = Directory.GetFiles(directory, "*.jsonl").SelectMany(File.ReadAllLines)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        var data = rows.Single(row => row.GetProperty("eventName").GetString() == "explicit.capture").GetProperty("data");
        check(data.GetProperty("nested").GetProperty("state").GetString() == "before"
            && data.GetProperty("values")[0].GetInt32() == 4, "explicit JSON diagnostic capture owns an immutable snapshot");
        check(data.GetProperty("bytes").GetString() == "AQID"
            && data.GetProperty("unsignedEnum").GetUInt64() == ulong.MaxValue
            && data.GetProperty("nullValue").ValueKind == JsonValueKind.Null,
            "explicit fields retain default JSON byte-array, unsigned-enum and null representations");
        check(data.GetProperty("uri").GetString() == "https://example.invalid/resource"
            && !rows.Any(row => row.GetRawText().Contains("serializationError")),
            "explicit diagnostic fields serialize without runtime reflection errors");
        var bytes = rows.Single(row => row.GetProperty("eventName").GetString() == "explicit.bytes").GetProperty("data");
        check(bytes.GetProperty("context").GetProperty("operation").GetString() == "read"
            && bytes.GetProperty("hex").GetString() == "0102", "protocol-byte context retains explicit nested fields");
        DiagnosticLog.ResetForTests();
    }

    private enum LargeValue : ulong { Maximum = ulong.MaxValue }
}
