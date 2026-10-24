using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using HeyMelody.Diagnostics;

if (OperatingSystem.IsWindows()) NativeMethods.SetErrorMode(3);

if (args is ["--owner"])
{
    Console.WriteLine("READY");
    Console.ReadLine();
    return 0;
}
if (args is ["--consume", var dataRoot])
{
    DiagnosticPaths.DataRootOverride = dataRoot;
    Console.WriteLine(DiagnosticSettingsStore.Consume() is null ? "0" : "1");
    return 0;
}

var root = Path.Combine(Path.GetTempPath(), "HeyMelody-diagnostics-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
DiagnosticPaths.DataRootOverride = root;
int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name); passed++;
}
DiagnosticSessionInfo Session(string name) => new()
{
    Enabled = true, SessionId = Guid.NewGuid().ToString("N"), Directory = Path.Combine(root, name),
    Components = [DiagnosticComponents.Main], OwnerProcessId = Environment.ProcessId,
    OwnerStartTimeUtcTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
    UserSessionKey = DiagnosticPaths.UserSessionKey
};
List<JsonElement> ReadLogs(string directory) => Directory.GetFiles(directory, "*.jsonl")
    .Order().SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();
Process Child(params string[] childArgs)
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (var value in childArgs) start.ArgumentList.Add(value);
    return Process.Start(start) ?? throw new IOException("Could not start fixture");
}
try
{
    var defaults = DiagnosticSettingsStore.Load();
    Check(defaults.Pending is null && defaults.SelectedComponents.SequenceEqual(DiagnosticComponents.All), "default off and all five components selected");
    Check(!Directory.Exists(DiagnosticPaths.LogRoot), "reading settings creates no log directory");
    DiagnosticSettingsStore.Save([DiagnosticComponents.Main, DiagnosticComponents.Host], DiagnosticArmIntent.ArmNew);
    var pendingId = DiagnosticSettingsStore.Load().Pending!.RequestId;
    var consumed = DiagnosticSettingsStore.Consume();
    Check(consumed?.RequestId == pendingId && DiagnosticSettingsStore.Consume() is null, "one-shot request consumed exactly once");
    DiagnosticSettingsStore.Save([DiagnosticComponents.Host], DiagnosticArmIntent.Preserve);
    Check(DiagnosticSettingsStore.Load().Pending is null, "stale preserve save cannot rearm consumed request");
    DiagnosticSettingsStore.Save([DiagnosticComponents.Host], DiagnosticArmIntent.ArmNew);
    DiagnosticSettingsStore.Save([DiagnosticComponents.Main], DiagnosticArmIntent.Preserve);
    Check(DiagnosticSettingsStore.Load().Pending!.Components.SequenceEqual(new[] { DiagnosticComponents.Main }), "selection edits update an existing pending request");
    DiagnosticSettingsStore.Save([], DiagnosticArmIntent.CancelPending);
    Check(DiagnosticSettingsStore.Load().Pending is null, "cancel removes pending request");
    bool emptyRejected = false;
    try { DiagnosticSettingsStore.Save([], DiagnosticArmIntent.ArmNew); } catch (InvalidOperationException) { emptyRejected = true; }
    Check(emptyRejected, "cannot arm an empty component selection");

    DiagnosticSettingsStore.Save(DiagnosticComponents.All, DiagnosticArmIntent.ArmNew);
    var claimers = Enumerable.Range(0, 6).Select(_ => Child("--consume", root)).ToArray();
    int claims = 0;
    foreach (var child in claimers)
    {
        using (child)
        {
            var output = await child.StandardOutput.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(child.ExitCode == 0, "isolated contender exits normally");
            claims += int.Parse(output.Trim());
        }
    }
    Check(claims == 1 && DiagnosticSettingsStore.Load().Pending is null, "atomic consume across six processes");

    var beforeOpen = File.ReadAllText(DiagnosticPaths.SettingsFile);
    var opened = new List<string>();
    DiagnosticPaths.OpenLogDirectory(opened.Add);
    DiagnosticPaths.OpenLogDirectory(opened.Add);
    Check(opened.Count == 2 && opened.All(path => path == DiagnosticPaths.LogRoot) && Directory.Exists(DiagnosticPaths.LogRoot), "open fixed log root creates missing directory and supports repeated opens");
    Check(File.ReadAllText(DiagnosticPaths.SettingsFile) == beforeOpen && !DiagnosticLog.HasSession, "opening directory neither arms logging nor creates a session");
    bool openFailed = false;
    try { DiagnosticPaths.OpenLogDirectory(_ => throw new IOException("fixture shell failed")); } catch (IOException) { openFailed = true; }
    Check(openFailed, "directory opener failure is available to the UI");

    DiagnosticLog.InitializeIdentity(DiagnosticComponents.Main, "main");
    var session = Session("format");
    DiagnosticLog.Configure(session);
    byte[] bytes = [0, 0x81, 0x14, 0xFF];
    DiagnosticLog.Bytes("Protocol", "wire.rx", bytes, new { command = "0x0114" });
    bytes[0] = 0xAA;
    var operation = Guid.NewGuid().ToString("N");
    using (DiagnosticLog.BeginOperation(operation))
    {
        await Task.Yield();
        DiagnosticLog.Debug("Fixture", "correlation", "中文", new { value = 42 });
    }
    Check(DiagnosticLog.CurrentCorrelationId is null, "operation scope restored across await");
    try { throw new InvalidDataException("outer", new IOException("inner")); }
    catch (Exception error) { DiagnosticLog.Error("Fixture", "exception", "failure", error); }
    DiagnosticLog.Stop("format completed");
    var logs = ReadLogs(session.Directory!);
    Check(logs.All(row => row.GetProperty("schemaVersion").GetInt32() == 1 && row.GetProperty("sessionId").GetString() == session.SessionId), "all records share version and session");
    Check(logs.Single(row => row.GetProperty("eventName").GetString() == "wire.rx").GetProperty("data").GetProperty("hex").GetString() == "008114FF", "RX bytes copied before receive buffer reuse");
    Check(logs.Single(row => row.GetProperty("eventName").GetString() == "correlation").GetProperty("correlationId").GetString() == operation, "operation correlation recorded without changing request IDs");
    var exception = logs.Single(row => row.GetProperty("eventName").GetString() == "exception").GetProperty("exception").GetString()!;
    Check(exception.Contains("inner") && exception.Contains("outer") && exception.Contains("Program"), "complete inner exception and stack retained");

    DiagnosticLog.ResetForTests();
    DiagnosticLog.InitializeIdentity(DiagnosticComponents.Main, "main");
    session = Session("rotation");
    DiagnosticLog.Configure(session, rotationBytes: 1024);
    Parallel.For(0, 100, index => DiagnosticLog.Debug("Fixture", "concurrent", "entry", new { index }));
    DiagnosticLog.Stop("rotation complete");
    logs = ReadLogs(session.Directory!);
    Check(logs.Count(row => row.GetProperty("eventName").GetString() == "concurrent") == 100 && Directory.GetFiles(session.Directory!, "*.jsonl").Length > 1, "concurrent writes rotate with no lost events");
    Check(logs.Select(row => row.GetProperty("sequence").GetInt64()).Distinct().Count() == logs.Count, "sequence unique across file parts");

    DiagnosticLog.ResetForTests();
    DiagnosticLog.InitializeIdentity(DiagnosticComponents.FitTest, "main");
    session = Session("filtered");
    DiagnosticLog.Configure(session);
    DiagnosticLog.Debug("Fixture", "excluded", "should not be written");
    DiagnosticLog.Stop("filter complete");
    Check(!Directory.Exists(session.Directory!), "unchecked component creates no runtime log file");

    DiagnosticLog.ResetForTests();
    DiagnosticLog.InitializeIdentity(DiagnosticComponents.Main, "main");
    var blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "file");
    session = Session("blocked");
    DiagnosticLog.Configure(session);
    Check(!DiagnosticLog.IsEnabled && DiagnosticLog.LastFailure is not null, "file creation failure disables writer and publishes diagnostic failure");
    var formattedFailure = DiagnosticLog.FormatFailureLine(DiagnosticLog.LastFailure!);
    Check(DiagnosticLog.TryParseFailureLine(formattedFailure, out var parsedFailure) && parsedFailure!.Component == DiagnosticComponents.Main,
        "worker failure side channel roundtrips independently of stdout RPC");

    DiagnosticLog.ResetForTests();
    DiagnosticLog.InitializeIdentity(DiagnosticComponents.Main, "main");
    session = Session("stale"); session.OwnerStartTimeUtcTicks--;
    Check(!DiagnosticRuntime.TryOpenOwner(session, out _), "owner start-time mismatch rejects stale descriptor");
    using (var owner = Child("--owner"))
    {
        Check(await owner.StandardOutput.ReadLineAsync() == "READY", "owner fixture ready");
        session = Session("owner-cycle");
        session.OwnerProcessId = owner.Id; session.OwnerStartTimeUtcTicks = owner.StartTime.ToUniversalTime().Ticks;
        DiagnosticSettingsStore.AtomicWrite(DiagnosticPaths.ReadyFile, session);
        using var lifecycle = DiagnosticRuntime.InitializeClient(DiagnosticComponents.Main, () => throw new Exception("must not launch"), () => true);
        Check(DiagnosticLog.IsEnabled, "component joins ready owner before rendering");
        DiagnosticLog.Debug("Fixture", "beforeOwnerExit", "recorded");
        owner.StandardInput.WriteLine();
        await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var deadline = Stopwatch.StartNew();
        while ((DiagnosticLog.IsEnabled || DiagnosticLog.HasSession) && deadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
        Check(!DiagnosticLog.IsEnabled && !DiagnosticLog.HasSession, "owner exit stops component recording");
        DiagnosticSettingsStore.AtomicWrite(DiagnosticPaths.ReadyFile, Session("next-cycle"));
        DiagnosticLog.Debug("Fixture", "afterOwnerExit", "must not follow new owner");
        logs = ReadLogs(session.Directory!);
        Check(logs.Any(row => row.GetProperty("eventName").GetString() == "beforeOwnerExit")
            && !logs.Any(row => row.GetProperty("eventName").GetString() == "afterOwnerExit"), "old component never follows next descriptor");
    }
    await WriterFailureChecks.RunAsync(root, Check);
    Console.WriteLine($"Diagnostics checks passed: {passed}; no Bluetooth or Explorer invoked.");
    return 0;
}

finally
{
    DiagnosticLog.ResetForTests();
    DiagnosticPaths.DataRootOverride = null;
    // Keep fixture output for diagnosis; print only the directory, never the full protocol log.
    Console.WriteLine("Fixture directory: " + root);
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll")]
    internal static extern uint SetErrorMode(uint mode);
}
