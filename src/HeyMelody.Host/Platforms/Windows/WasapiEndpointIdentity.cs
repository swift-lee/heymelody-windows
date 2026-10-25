#if HEYMELODY_WINDOWS
using System.Runtime.InteropServices;
using System.Text;
using static HeyMelodyWindows.Host.WasapiNative;

namespace HeyMelodyWindows.Host;

internal static partial class EndpointIdentity
{
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

#endif
