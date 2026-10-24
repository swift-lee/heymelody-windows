using System.Diagnostics;
using System.Text.Json;

namespace HeyMelody.Diagnostics;

public static class DiagnosticRuntime
{
    public static IDisposable InitializeHost()
    {
        DiagnosticLog.InitializeIdentity(DiagnosticComponents.Host, "backend");
        using var current = Process.GetCurrentProcess();
        var session = new DiagnosticSessionInfo
        {
            OwnerProcessId = Environment.ProcessId, OwnerStartTimeUtcTicks = current.StartTime.ToUniversalTime().Ticks,
            UserSessionKey = DiagnosticPaths.UserSessionKey
        };
        try
        {
            var request = DiagnosticSettingsStore.Consume();
            if (request is not null)
            {
                session.Enabled = true;
                session.SessionId = Guid.NewGuid().ToString("N");
                session.RequestId = request.RequestId;
                session.Components = request.Components;
                session.Directory = Path.Combine(DiagnosticPaths.LogRoot,
                    session.StartedAtUtc.ToString("yyyyMMdd-HHmmss") + "-" + session.SessionId);
                Directory.CreateDirectory(session.Directory);
                DiagnosticSettingsStore.AtomicWrite(Path.Combine(session.Directory, "session.json"), session);
            }
            DiagnosticLog.Configure(session);
            session.Failure = DiagnosticLog.LastFailure;
            DiagnosticSettingsStore.AtomicWrite(DiagnosticPaths.ReadyFile, session);
        }
        catch (Exception error)
        {
            DiagnosticLog.PublishFailure("未能开始本轮诊断日志，请检查日志目录和设置后重新安排。", error);
            session.Failure = DiagnosticLog.LastFailure;
            try { DiagnosticSettingsStore.AtomicWrite(DiagnosticPaths.ReadyFile, session); } catch { }
        }
        return new RuntimeLease(session, null, true);
    }

    public static IDisposable InitializeWorker(string role)
    {
        DiagnosticLog.InitializeIdentity(DiagnosticComponents.Host, role, reportToStandardError: true);
        return Join(ensureBackend: false);
    }

    public static IDisposable InitializeClient(string component, Action? startBackend = null, Func<bool>? backendRunning = null)
    {
        DiagnosticLog.InitializeIdentity(component, "main");
        return Join(ensureBackend: true, startBackend, backendRunning);
    }

    private static IDisposable Join(bool ensureBackend, Action? startBackend = null, Func<bool>? backendRunning = null)
    {
        var started = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        Process? pendingOwner = null;
        RuntimeLease? pendingLease = null;
        try
        {
            if (ensureBackend && !(backendRunning ?? DiagnosticBackendLauncher.IsRunning)())
                (startBackend ?? DiagnosticBackendLauncher.Start)();
            do
            {
                var session = ReadReady();
                if (session is not null && TryOpenOwner(session, out var owner))
                {
                    pendingOwner = owner;
                    pendingLease = new RuntimeLease(session, owner, false);
                    pendingOwner = null;
                    if (!pendingLease.TryConfigure()) { pendingLease.Dispose(); return new RuntimeLease(null, null, false); }
                    if (session.Failure is not null) DiagnosticLog.ReportExternalFailure(session.Failure);
                    DiagnosticLog.Info("Runtime", "bootstrap.completed", "Logging initialized before UI/rendering",
                        new { startedAtUtc = started, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds });
                    return pendingLease;
                }
                if (!ensureBackend) break;
                Thread.Sleep(25);
            } while (timer.Elapsed < TimeSpan.FromSeconds(5));
            DiagnosticLog.PublishFailure("未能取得本轮后台的日志启动决议；本组件启动日志存在缺口。请退出托盘后台后重试。");
        }
        catch (Exception error)
        {
            pendingLease?.Dispose();
            pendingOwner?.Dispose();
            DiagnosticLog.PublishFailure("未能开始本组件日志；应用将继续启动。", error);
        }
        return new RuntimeLease(null, null, false);
    }

    public static DiagnosticSessionInfo? ReadCurrentSession()
    {
        var session = ReadReady();
        if (session is null || !TryOpenOwner(session, out var owner)) return null;
        owner!.Dispose();
        return session;
    }

    private static DiagnosticSessionInfo? ReadReady()
    {
        try
        {
            var session = JsonSerializer.Deserialize<DiagnosticSessionInfo>(File.ReadAllText(DiagnosticPaths.ReadyFile));
            return session is { SchemaVersion: 1 } && session.UserSessionKey == DiagnosticPaths.UserSessionKey ? session : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static bool TryOpenOwner(DiagnosticSessionInfo session, out Process? owner)
    {
        owner = null;
        Process? candidate = null;
        try
        {
            if (session.UserSessionKey != DiagnosticPaths.UserSessionKey) return false;
            candidate = Process.GetProcessById(session.OwnerProcessId);
            if (candidate.HasExited || candidate.StartTime.ToUniversalTime().Ticks != session.OwnerStartTimeUtcTicks
                || candidate.SessionId != Process.GetCurrentProcess().SessionId)
                return false;
            owner = candidate;
            candidate = null;
            return true;
        }
        catch (Exception) { owner = null; return false; }
        finally { candidate?.Dispose(); }
    }

    private sealed class RuntimeLease : IDisposable
    {
        private readonly DiagnosticSessionInfo? _session;
        private readonly Process? _owner;
        private readonly bool _host;
        private readonly object _gate = new();
        private bool _ownsLogger;
        private int _disposed;
        internal RuntimeLease(DiagnosticSessionInfo? session, Process? owner, bool host)
        {
            _session = session; _owner = owner; _host = host; _ownsLogger = host;
            if (owner is null) return;
            try { owner.Exited += OwnerExited; owner.EnableRaisingEvents = true; }
            catch { owner.Exited -= OwnerExited; throw; }
        }
        internal bool TryConfigure()
        {
            lock (_gate)
            {
                if (_disposed != 0 || _owner is null || _owner.HasExited || _session is null) return false;
                DiagnosticLog.Configure(_session);
                _ownsLogger = true;
                if (_owner.HasExited) { Dispose(); return false; }
                return true;
            }
        }
        private void OwnerExited(object? sender, EventArgs args) => Dispose();
        public void Dispose()
        {
            lock (_gate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                if (_owner is not null) { _owner.Exited -= OwnerExited; _owner.Dispose(); }
                if (_ownsLogger) DiagnosticLog.Stop(_host ? "Host lifecycle ended" : "Component or owning Host exited");
                if (_host && _session is not null)
                    try { if (ReadReady()?.OwnerEpoch == _session.OwnerEpoch) File.Delete(DiagnosticPaths.ReadyFile); } catch { }
            }
        }
    }
}
