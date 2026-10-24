using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace HeyMelody.Diagnostics;

public static partial class DiagnosticRuntime
{
    private const string LaunchOwnerVariable = "HEYMELODY_COMPONENT_OWNER";
    private sealed class LaunchOwner
    {
        public int Pid { get; set; }
        public long StartTicks { get; set; }
        public string SessionKey { get; set; } = "";
    }
    private static bool _ignoreLaunchBinding = Assembly.GetEntryAssembly()?.GetName().Name == DiagnosticComponents.Main;
    private static DiagnosticSessionInfo? _joinedOwner;
    private static readonly Lazy<(bool Present, LaunchOwner? Owner)> Launch = new(() =>
    {
        var raw = Environment.GetEnvironmentVariable(LaunchOwnerVariable);
        if (raw is null) return (false, null);
        try { return (true, JsonSerializer.Deserialize<LaunchOwner>(raw)); }
        catch (JsonException) { return (true, null); }
    });

    public static bool HasBoundLaunch => !_ignoreLaunchBinding && Launch.Value.Present;
    public static event Action? BoundOwnerExited;

    internal static void ResetLaunchStateForTests()
    {
        _joinedOwner = null;
        _ignoreLaunchBinding = Assembly.GetEntryAssembly()?.GetName().Name == DiagnosticComponents.Main;
    }

    public static bool IsBoundLaunchExpired
    {
        get
        {
            if (!HasBoundLaunch) return false;
            var owner = Launch.Value.Owner;
            var ready = ReadReady();
            if (owner is null || ready is null || !MatchesLaunchOwner(ready) || !TryOpenOwner(ready, out var process)) return true;
            process!.Dispose();
            return false;
        }
    }

    /// <summary>An owned editor belongs to the original Host even when it has not reached its entry point yet.</summary>
    public static void BindChildStart(ProcessStartInfo start)
    {
        // Logging stays on its first owner. An active main UI may legitimately recover from a Host crash,
        // so a new owned editor uses the currently verified owner instead of that old logging session.
        var owner = HasBoundLaunch ? _joinedOwner ?? DiagnosticLog.Session
            : ReadCurrentSession() ?? _joinedOwner ?? DiagnosticLog.Session;
        // Always mark an owned launch. Missing owner metadata must not silently turn it into an auto-starting editor.
        start.Environment[LaunchOwnerVariable] = JsonSerializer.Serialize(new LaunchOwner
        {
            Pid = owner?.OwnerProcessId ?? 0, StartTicks = owner?.OwnerStartTimeUtcTicks ?? 0,
            SessionKey = owner?.UserSessionKey ?? ""
        });
    }

    private static bool MatchesLaunchOwner(DiagnosticSessionInfo session)
    {
        if (!HasBoundLaunch) return true;
        return Launch.Value.Owner is { } owner && owner.Pid == session.OwnerProcessId
            && owner.StartTicks == session.OwnerStartTimeUtcTicks && owner.SessionKey == session.UserSessionKey;
    }

    private static void PublishBoundOwnerExited()
    {
        foreach (Action handler in BoundOwnerExited?.GetInvocationList() ?? [])
            try { handler(); } catch { }
    }
}
