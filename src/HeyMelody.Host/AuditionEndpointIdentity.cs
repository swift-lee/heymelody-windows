using System.Runtime.InteropServices;
using System.Text;
using HeyMelody.Diagnostics;
using static HeyMelodyWindows.Host.WasapiNative;

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

internal static class EndpointIdentity
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
                DiagnosticLog.Debug("host.audio", "endpointResolutionFailed", "Audition endpoint identity lookup failed",
                    new { trace.Phase, trace.Attempts, trace.EndpointCount, trace.MatchCount,
                        trace.LastNativeFailurePhase, trace.LastNativeFailureHResult, error.HResult, error.Message });
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

    internal static IDevice Resolve(IDeviceEnumerator enumerator, ulong address, EndpointTrace trace)
    {
        IDeviceCollection? collection = null;
        var devices = new List<IDevice>();
        IDevice? selected = null;
        try
        {
            trace.Phase = "Identity.EnumAudioEndpoints";
            Check(enumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* ACTIVE */, out collection));
            trace.Phase = "Identity.GetEndpointCount";
            Check(collection.GetCount(out var count));
            trace.EndpointCount = (int)count;
            if (count > 1024) throw new InvalidOperationException("Endpoint count exceeds bound.");
            var identities = new List<IReadOnlyList<string>>();
            for (uint i = 0; i < count; i++)
            {
                trace.Phase = "Identity.GetEndpoint";
                Check(collection.Item(i, out var device)); devices.Add(device);
                identities.Add(AncestryForEndpoint(enumerator, device, trace));
            }
            trace.Phase = "Identity.MatchExactSink";
            trace.MatchCount = identities.Count(chain => chain.Any(id => AuditionEndpointAddress.TryParse(id, out var candidate) && candidate == address));
            selected = devices[SelectIndex(identities, address)];
            return selected;
        }
        finally
        {
            foreach (var device in devices) if (!ReferenceEquals(device, selected)) Release(device);
            Release(collection);
        }
    }

    private static IReadOnlyList<string> AncestryForEndpoint(IDeviceEnumerator enumerator, IDevice endpoint, EndpointTrace trace)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in Ancestors(InstanceId(endpoint, trace, "Endpoint"))) result.Add(id);
        // Some endpoint PKEYs describe SWD\MMDEVAPI nodes. Their topology connector identifies
        // the physical adapter; read its SDK-defined instance property and traverse that devnode.
        object? topologyObject = null;
        try
        {
            trace.Phase = "Identity.ActivateTopology";
            if (trace.Failed(endpoint.Activate(TopologyId, 1, IntPtr.Zero, out topologyObject))) return result.ToArray();
            var topology = (IDeviceTopology)topologyObject;
            trace.Phase = "Identity.GetConnectorCount";
            if (trace.Failed(topology.GetConnectorCount(out var count)) || count > 64) return result.ToArray();
            for (uint i = 0; i < count; i++)
            {
                IConnector? connector = null; IDevice? adapter = null;
                try
                {
                    trace.Phase = "Identity.GetConnector";
                    if (trace.Failed(topology.GetConnector(i, out connector))) continue;
                    trace.Phase = "Identity.GetConnectedAdapterId";
                    if (trace.Failed(connector.GetDeviceIdConnectedTo(out var adapterId))) continue;
                    trace.Phase = "Identity.GetAdapter";
                    if (trace.Failed(enumerator.GetDevice(adapterId, out adapter))) continue;
                    foreach (var id in Ancestors(InstanceId(adapter, trace, "Adapter"))) result.Add(id);
                }
                finally { Release(adapter); Release(connector); }
            }
        }
        finally { Release(topologyObject); }
        return result.ToArray();
    }

    private static string? InstanceId(IDevice device, EndpointTrace trace, string kind)
    {
        IPropertyStore? properties = null;
        try
        {
            trace.Phase = "Identity." + kind + ".OpenPropertyStore";
            if (trace.Failed(device.OpenPropertyStore(0 /* STGM_READ */, out properties))) return null;
            trace.Phase = "Identity." + kind + ".ReadInstanceId";
            int hr = properties.GetValue(DeviceInstanceId, out var value);
            trace.Failed(hr);
            try { return hr >= 0 && value.Type == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.Pointer) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally { Release(properties); }
    }

    private static IReadOnlyList<string> Ancestors(string? instanceId)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(instanceId)) return result;
        result.Add(instanceId);
        if (CM_Locate_DevNodeW(out var node, instanceId, 0) != 0) return result;
        var visited = new HashSet<uint> { node };
        for (int depth = 0; depth < 64; depth++)
        {
            if (CM_Get_Parent(out var parent, node, 0) != 0 || !visited.Add(parent)) break;
            if (CM_Get_Device_ID_Size(out var length, parent, 0) != 0 || length is 0 or > 4096) break;
            var text = new StringBuilder((int)length + 1);
            if (CM_Get_Device_IDW(parent, text, length + 1, 0) != 0) break;
            result.Add(text.ToString()); node = parent;
        }
        return result;
    }
}
