using System.Runtime.InteropServices;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Host;

// Adapted from HeyMelody.Audio/EndpointIdentity.cs to match the selected peer directly through A2DP ancestry, without a driver-manager resolver.

internal sealed class EndpointMatchException(int endpointCount, int matchCount) : InvalidOperationException("Exact endpoint match unavailable.")
{
    internal int EndpointCount { get; } = endpointCount;
    internal int MatchCount { get; } = matchCount;
}
internal sealed class EndpointTrace
{
    internal string Phase { get; set; } = "Identity.Resolve";
    internal int Attempts { get; set; }
    internal int? EndpointCount { get; set; }
    internal int? MatchCount { get; set; }
    internal string? LastNativeFailurePhase { get; private set; }
    internal int? LastNativeFailureHResult { get; private set; }
    internal bool Failed(int hr)
    {
        if (hr >= 0) return false;
        DiagnosticLog.Debug("host.audio", "endpointNativeFailure", "WASAPI endpoint identity query returned a failing HRESULT",
            new { Phase, Attempts, hresult = hr, hresultHex = $"0x{hr:X8}" });
        LastNativeFailurePhase = Phase; LastNativeFailureHResult = hr; return true;
    }
}

internal static partial class EndpointIdentity
{
    // Only identity enumeration is repeated. No activation of an audio client, Initialize, Start or setter retries.
    internal static T WaitForStable<T>(Func<T> resolve, CancellationToken token, EndpointTrace trace,
        Action<CancellationToken>? wait = null)
    {
        for (int attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested(); trace.Attempts = attempt;
            trace.EndpointCount = null; trace.MatchCount = null;
            try { return resolve(); }
            catch (EndpointMatchException e) when (e.MatchCount == 0 && attempt < 3)
            {
                DiagnosticLog.Debug("host.audio", "endpointRetry", "No exact audition endpoint yet; retrying identity lookup",
                    new { attempt, e.EndpointCount, e.MatchCount });
                if (wait is not null) wait(token);
                else if (token.WaitHandle.WaitOne(250)) token.ThrowIfCancellationRequested();
            }
            catch (COMException e) when (attempt < 3 && e.HResult is unchecked((int)0x88890004) or unchecked((int)0x80070490))
            {
                DiagnosticLog.Debug("host.audio", "endpointRetry", "Audition endpoint changed during identity lookup",
                    new { attempt, trace.Phase, e.HResult, e.Message });
                // AUDCLNT_E_DEVICE_INVALIDATED or HRESULT_FROM_WIN32(ERROR_NOT_FOUND).
                if (wait is not null) wait(token);
                else if (token.WaitHandle.WaitOne(250)) token.ThrowIfCancellationRequested();
            }
            catch (Exception error)
            {
                DiagnosticLog.Warning("host.audio", "endpointResolutionFailed", "Audition endpoint identity lookup failed", error,
                    new { trace.Phase, trace.Attempts, trace.EndpointCount, trace.MatchCount,
                        trace.LastNativeFailurePhase, trace.LastNativeFailureHResult, error.HResult });
                throw;
            }
        }
    }

    // Pure selection seam: no device names, default endpoint, substring IDs or container-only inference.
    internal static int SelectIndex(IReadOnlyList<IReadOnlyList<string>> endpointAncestors, ulong address)
    {
        var matches = Enumerable.Range(0, endpointAncestors.Count).Where(i =>
            endpointAncestors[i].Any(id => AuditionEndpointAddress.TryParse(id, out var candidate) && candidate == address)).ToArray();
        if (matches.Length != 1) throw new EndpointMatchException(endpointAncestors.Count, matches.Length);
        return matches[0];
    }

}
