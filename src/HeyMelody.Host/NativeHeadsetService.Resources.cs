using System.IO.Compression;
using System.Text.Json;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Resources;

namespace HeyMelodyWindows.Host;

public sealed partial class NativeHeadsetService
{
    private ResourceCatalog? _catalog;
    private string _stableKey = "";
    private long _resourceRevision, _generation, _appMarker = 17006004;
    private string? _observedProductId;
    private byte? _observedColor;
    internal void ConfigureResources(WorkerRequest request)
    {
        if (request.CatalogGzip is null) return;
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(request.CatalogGzip)), CompressionMode.Decompress);
        using var target = new MemoryStream(); var buffer = new byte[8192];
        int read;
        while ((read = gzip.Read(buffer)) > 0)
        { if (target.Length + read > 8 * 1024 * 1024) throw new InvalidDataException(); target.Write(buffer, 0, read); }
        _catalog = ResourceCatalog.Parse(target.ToArray(), false);
        _resourceRevision = request.ResourceRevision;
        _appMarker = request.AppMarker; _stableKey = request.StableKey; _generation = request.Generation;
        _observedProductId = request.ObservedProductId; _observedColor = request.ObservedColor;
    }
    private DailyClient CreateDailyClient(ulong address)
    {
        if (_catalog is null) return new(new PeerConnector(_bluetooth, address, RecordSubmission));
        var services = _catalog.Profiles.Values.Where(p => p.CanUseControlTransport).Select(p => Guid.Parse(p.Uuid!)).Distinct().ToArray();
        return new(new PeerConnector(_bluetooth, address, RecordSubmission, services), id => _catalog.Find(id) is { CanUseControlTransport: true } p
            ? DeviceProfile.FromResource(p.Raw, _catalog.Sha256) : null, _appMarker, _observedProductId, _observedColor);
    }
    internal async Task<UiSnapshot> SetObservedColorAsync(string productId, byte color, CancellationToken token) =>
        _client is null ? throw new InvalidOperationException("耳机连接已变化。") : Map(await _client.SetObservedColorAsync(productId, color, token));
    private IReadOnlyList<UiAncMode> BuildAncModes(DailySnapshot snapshot)
    {
        var raw = _catalog is null ? DeviceModelResolver.FindByProductId(snapshot.ProductId ?? "")?.RawFunctions
            : _catalog.Find(snapshot.ProductId)?.Functions;
        var definitions = raw is { ValueKind: JsonValueKind.Object } f && f.TryGetProperty("noiseReductionMode", out var modes)
            && modes.ValueKind == JsonValueKind.Array ? modes.EnumerateArray().ToArray() : [];
        return snapshot.AncOptions.GroupBy(x => x.Primary).Select(group =>
        {
            int? type = definitions.FirstOrDefault(x => x.TryGetProperty("protocolIndex", out var v) && v.GetInt32() == group.Key)
                is var d && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("modeType", out var mt) ? mt.GetInt32() : null;
            var (name, icon) = type switch { 3 => ("弱降噪", "anc"), 4 => ("强降噪", "anc"),
                5 => ("降噪", "anc"), 2 => ("通透", "transparency"), 6 => ("聆听", "transparency"),
                1 => ("关闭", "off"), 7 => ("智能降噪", "smart"), 10 => ("自适应", "adaptive"),
                _ => (group.First().Name, "anc") };
            var options = group.Select(x => new UiOption((x.Primary << 8) | x.Level, x.Name)).ToArray();
            UiAncToggle? toggle = null;
            if (type == 2 && d.TryGetProperty("childrenMode", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                var adjustments = children.EnumerateArray().ToArray();
                if (adjustments is [var off, var on] && AncAdjustment(off, 2) is int offId && AncAdjustment(on, 6) is int onId)
                {
                    int offKey = (group.Key << 8) | offId, onKey = (group.Key << 8) | onId;
                    if (offKey != onKey && options.Length == 2 && options.Any(x => x.Key == offKey) && options.Any(x => x.Key == onKey))
                        toggle = new(offKey, onKey, "强化人声", "弱化环境声的同时突出人声");
                }
            }
            return new UiAncMode(group.Key, name, icon, options)
            {
                Description = type switch
                {
                    6 => "减弱周围噪音基础上保留人声/广播声",
                    10 => "实时融合主动降噪与通透模式，根据所处环境噪声的变化，控制耳机中的噪声水平",
                    _ => ""
                },
                Toggle = toggle
            };
        }).ToArray();
    }
    private static int? AncAdjustment(JsonElement source, int modeType) => source.ValueKind == JsonValueKind.Object
        && source.TryGetProperty("modeType", out var type) && type.ValueKind == JsonValueKind.Number
        && type.TryGetInt32(out int value) && value == modeType
        && source.TryGetProperty("protocolIndex", out var index) && index.ValueKind == JsonValueKind.Number
        && index.TryGetInt32(out int id) && id is >= 0 and <= byte.MaxValue ? id : null;
    private IReadOnlyList<UiOption> SpatialOptions(DailySnapshot snapshot)
    {
        var functions = _catalog?.Find(snapshot.ProductId)?.Functions;
        int[] types = functions is { ValueKind: JsonValueKind.Object } f && f.TryGetProperty("spatialTypes", out var values)
            && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Select(x => x.GetInt32()).ToArray() : [0,1,2];
        return types.Where(type => type is >= 0 and <= 2 && (snapshot.Capabilities.SpatialRoute != "legacy" || type <= 1))
            .Distinct().Select(type => new UiOption(type, type == 0 ? "关闭" : type == 1 ? "固定空间音频" : "头部跟踪")).ToArray();
    }
}
