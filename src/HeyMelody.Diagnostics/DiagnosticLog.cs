using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HeyMelody.Diagnostics;

public static class DiagnosticLog
{
    private static readonly AsyncLocal<string?> Correlation = new();
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DiagnosticFailure> Failures = [];
    private static DiagnosticWriter? _writer;
    private static DiagnosticSessionInfo? _session;
    private static bool _reportToStandardError;
    private static int _initialized;
    public const string FailurePrefix = "HEYMELODY_DIAGNOSTIC_FAILURE:";
    public static string Component { get; private set; } = Assembly.GetEntryAssembly()?.GetName().Name ?? "HeyMelody";
    public static string Role { get; private set; } = "main";
    public static string ProcessInstanceId { get; } = Guid.NewGuid().ToString("N");
    public static string? SessionId => _session?.SessionId;
    public static DiagnosticSessionInfo? Session => _session;
    public static bool IsEnabled => Volatile.Read(ref _writer)?.IsEnabled == true;
    public static bool HasSession => _session is { Enabled: true };
    public static string? CurrentCorrelationId => Correlation.Value;
    public static DiagnosticFailure? LastFailure { get; private set; }
    public static event Action<DiagnosticFailure>? Failed;
    public static IReadOnlyList<DiagnosticFailure> GetFailures() { lock (Gate) return Failures.Values.ToArray(); }

    internal static void ResetForTests()
    {
        Stop("Test logging reset");
        lock (Gate) { Failures.Clear(); LastFailure = null; }
        Correlation.Value = null;
        _reportToStandardError = false;
        Failed = null;
    }

    internal static void InitializeIdentity(string component, string role, bool reportToStandardError = false)
    {
        Component = component;
        Role = role;
        _reportToStandardError = reportToStandardError;
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        Trace.Listeners.Add(new DiagnosticTraceListener());
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Write(DiagnosticLevel.Critical, "Runtime", "process.unhandled", "Unhandled process exception",
                new { args.IsTerminating }, args.ExceptionObject as Exception);
            Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            Error("Runtime", "task.unobserved", "Unobserved task exception", args.Exception);
    }

    internal static void Configure(DiagnosticSessionInfo session, long rotationBytes = 32L * 1024 * 1024, int capacity = 8192,
        Func<string, Stream>? streamFactory = null)
    {
        _session = session;
        if (!session.Enabled || !session.Components.Contains(Component) || session.Directory is null) return;
        try { _writer = new DiagnosticWriter(session, Component, Role, ProcessInstanceId, PublishFailure, rotationBytes, capacity, streamFactory); }
        catch (Exception error) { PublishFailure("无法创建日志文件，本组件日志未开始记录。", error); return; }
        Info("Runtime", "process.started", "Process joined diagnostic session", new
        {
            arguments = Environment.GetCommandLineArgs(), executable = Environment.ProcessPath,
            version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            operatingSystem = RuntimeInformation.OSDescription, osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), runtime = RuntimeInformation.FrameworkDescription,
            culture = System.Globalization.CultureInfo.CurrentCulture.Name, processStartedAtUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime()
        });
    }

    public static IDisposable BeginOperation(string? correlationId = null)
    {
        var previous = Correlation.Value;
        Correlation.Value = Guid.TryParse(correlationId, out var parsed) ? parsed.ToString("N")
            : HasSession ? Guid.NewGuid().ToString("N") : null;
        return new OperationLease(previous);
    }

    public static IDisposable SuspendOperation()
    {
        var previous = Correlation.Value;
        Correlation.Value = null;
        return new OperationLease(previous);
    }

    public static void Write(DiagnosticLevel level, string source, string eventName, string message,
        object? data = null, Exception? exception = null)
    {
        var writer = Volatile.Read(ref _writer);
        if (writer?.IsEnabled != true) return;
        JsonElement? captured = null;
        if (data is not null)
        {
            try { captured = JsonSerializer.SerializeToElement(data, data.GetType()); }
            catch (Exception error)
            {
                // A diagnostic object's getter must not change the result of the operation being diagnosed.
                captured = JsonSerializer.SerializeToElement(new { serializationError = error.ToString(), valueType = data.GetType().FullName });
            }
        }
        writer.Enqueue(new(DateTimeOffset.UtcNow, level, source, eventName, message, CurrentCorrelationId,
            Environment.CurrentManagedThreadId, captured, exception?.ToString()));
    }

    public static void Debug(string source, string eventName, string message, object? data = null)
        => Write(DiagnosticLevel.Debug, source, eventName, message, data);
    public static void Info(string source, string eventName, string message, object? data = null)
        => Write(DiagnosticLevel.Information, source, eventName, message, data);
    public static void Warning(string source, string eventName, string message, Exception? exception = null, object? data = null)
        => Write(DiagnosticLevel.Warning, source, eventName, message, data, exception);
    public static void Error(string source, string eventName, string message, Exception? exception = null, object? data = null)
        => Write(DiagnosticLevel.Error, source, eventName, message, data, exception);
    public static void Bytes(string source, string eventName, ReadOnlySpan<byte> bytes, object? data = null)
    {
        if (!IsEnabled) return;
        // Convert now, while the receive buffer is still owned by the caller.
        Debug(source, eventName, "Protocol bytes", new { length = bytes.Length, hex = Convert.ToHexString(bytes), context = data });
    }

    public static void Flush() => Volatile.Read(ref _writer)?.Flush(TimeSpan.FromSeconds(2));

    internal static void Stop(string reason)
    {
        Info("Runtime", "process.loggingStopped", reason);
        var writer = Interlocked.Exchange(ref _writer, null);
        _session = null;
        writer?.Dispose();
    }

    public static void ReportExternalFailure(DiagnosticFailure failure)
    {
        if (failure is null) return;
        lock (Gate)
        {
            Failures[failure.SessionId + ":" + failure.ProcessInstanceId] = failure;
            LastFailure = failure;
        }
        foreach (var handler in Failed?.GetInvocationList() ?? [])
            try { ((Action<DiagnosticFailure>)handler)(failure); } catch { }
    }

    internal static void PublishFailure(string message, Exception? error = null)
    {
        var failure = new DiagnosticFailure
        {
            Component = Component, Role = Role, ProcessInstanceId = ProcessInstanceId, SessionId = SessionId,
            Message = message, Detail = error?.ToString(), Directory = _session?.Directory
        };
        ReportExternalFailure(failure);
        if (_reportToStandardError)
            try { Console.Error.WriteLine(FormatFailureLine(failure)); } catch { }
    }

    public static string FormatFailureLine(DiagnosticFailure failure) => FailurePrefix + JsonSerializer.Serialize(failure);
    public static bool TryParseFailureLine(string line, out DiagnosticFailure? failure)
    {
        failure = null;
        if (!line.StartsWith(FailurePrefix, StringComparison.Ordinal) || line.Length > 65536) return false;
        try
        {
            failure = JsonSerializer.Deserialize<DiagnosticFailure>(line.AsSpan(FailurePrefix.Length));
            return failure is not null && !string.IsNullOrWhiteSpace(failure.Component)
                && !string.IsNullOrWhiteSpace(failure.ProcessInstanceId);
        }
        catch (JsonException) { return false; }
    }

    private sealed class OperationLease(string? previous) : IDisposable
    {
        public void Dispose() => Correlation.Value = previous;
    }
    private sealed class DiagnosticTraceListener : TraceListener
    {
        public override void Write(string? message) { if (message is not null) Debug("Trace", "trace.output", message); }
        public override void WriteLine(string? message) => Write(message);
    }
}

internal sealed record DiagnosticEntry(DateTimeOffset TimeUtc, DiagnosticLevel Level, string Source,
    string EventName, string Message, string? CorrelationId, int ThreadId, JsonElement? Data, string? Exception);
