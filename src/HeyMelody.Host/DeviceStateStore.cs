using System.Text.Json;
using HeyMelody.Diagnostics;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Host;

// These records deliberately contain no session identifiers, write permissions, transport
// observations, calibration handles or artwork leases. Restoring one cannot restore authority.
internal sealed record PresentationControl(string Id, string Label, string Kind, int? Value, string? Text,
    UiOption[] Options, int Minimum, int Maximum, int Step, string Description);
internal sealed record PresentationSection(string Id, string Header, bool DefaultExpanded,
    PresentationControl[] Controls, string Description);
internal sealed record PresentationHearingProfile(int? Uid, string Name, bool Current);
internal sealed record DevicePresentation
{
    public string ProductId { get; init; } = "";
    public byte? Color { get; init; }
    public string? Firmware { get; init; }
    public DateTimeOffset SampleTime { get; init; }
    public int CustomEqualizerCapacity { get; init; }
    public int[] EqualizerFrequencies { get; init; } = [];
    public UiAncMode[] AncModes { get; init; } = [];
    public PresentationSection[] FeatureSections { get; init; } = [];
    public bool? HearingEnabled { get; init; }
    public PresentationHearingProfile[] HearingProfiles { get; init; } = [];
    public int? Anc { get; init; }
    public int? AncPrimary { get; init; }
    public int? Equalizer { get; init; }
    public int? Spatial { get; init; }
    public bool? DualConnection { get; init; }
    public bool? GameMode { get; init; }
    public bool? HiRes { get; init; }
    public bool SpatialHiResMutex { get; init; }
    public UiOption[] AncOptions { get; init; } = [];
    public UiOption[] EqualizerOptions { get; init; } = [];
    public UiOption[] SpatialOptions { get; init; } = [];
    public UiEqEntry[] EqualizerEntries { get; init; } = [];
    public string[] Warnings { get; init; } = [];

    internal static DevicePresentation From(UiSnapshot snapshot, DateTimeOffset now) => new()
    {
        ProductId = snapshot.ProductId!.ToUpperInvariant(), Color = snapshot.Color, Firmware = snapshot.Firmware,
        SampleTime = snapshot.SampleTime == default ? now : snapshot.SampleTime,
        CustomEqualizerCapacity = snapshot.CustomEqualizerCapacity,
        EqualizerFrequencies = snapshot.EqualizerFrequencies.ToArray(),
        AncModes = snapshot.AncModes.Select(m => m with { Options = m.Options.ToArray() }).ToArray(),
        FeatureSections = snapshot.FeatureSections.Select(s => new PresentationSection(s.Id, s.Header, s.DefaultExpanded,
            s.Controls.Select(c => new PresentationControl(c.Id, c.Label, c.Kind, c.Value, c.Text,
                c.Options?.ToArray() ?? [], c.Minimum, c.Maximum, c.Step, c.Description)).ToArray(), s.Description)).ToArray(),
        HearingEnabled = snapshot.HearingEnabled,
        HearingProfiles = snapshot.HearingProfiles.Select(p => new PresentationHearingProfile(p.Uid, p.Name, p.Current)).ToArray(),
        Anc = snapshot.Anc, AncPrimary = snapshot.AncPrimary, Equalizer = snapshot.Equalizer, Spatial = snapshot.Spatial,
        DualConnection = snapshot.DualConnection, GameMode = snapshot.GameMode, HiRes = snapshot.HiRes,
        SpatialHiResMutex = snapshot.SpatialHiResMutex, AncOptions = snapshot.AncOptions.ToArray(),
        EqualizerOptions = snapshot.EqualizerOptions.ToArray(), SpatialOptions = snapshot.SpatialOptions.ToArray(),
        EqualizerEntries = snapshot.EqualizerEntries.Select(e => e with { Bands = e.Bands.ToArray() }).ToArray(),
        Warnings = snapshot.Warnings.ToArray()
    };

    internal UiSnapshot ToSnapshot() => new()
    {
        PresentationCached = true, PresentationSampleTime = SampleTime,
        ProductId = ProductId, Color = Color, Firmware = Firmware, CustomEqualizerCapacity = CustomEqualizerCapacity,
        EqualizerFrequencies = EqualizerFrequencies.ToArray(),
        AncModes = AncModes.Select(m => m with { Options = m.Options.ToArray() }).ToArray(),
        FeatureSections = FeatureSections.Select(s => new UiFeatureSection(s.Id, s.Header, s.DefaultExpanded,
            s.Controls.Select(c => new UiFeatureControl(c.Id, c.Label, c.Kind, c.Value, c.Text,
                c.Options.ToArray(), c.Minimum, c.Maximum, c.Step, CanWrite: false, Description: c.Description)).ToArray(), s.Description)).ToArray(),
        HearingEnabled = HearingEnabled,
        HearingProfiles = HearingProfiles.Select(p => new UiHearingProfile(p.Uid, p.Name, p.Current, CanApply: false)).ToArray(),
        Anc = Anc, AncPrimary = AncPrimary, Equalizer = Equalizer, Spatial = Spatial,
        DualConnection = DualConnection, GameMode = GameMode, HiRes = HiRes, SpatialHiResMutex = SpatialHiResMutex,
        AncOptions = AncOptions.ToArray(), EqualizerOptions = EqualizerOptions.ToArray(), SpatialOptions = SpatialOptions.ToArray(),
        EqualizerEntries = EqualizerEntries.Select(e => e with { Bands = e.Bands.ToArray() }).ToArray(), Warnings = Warnings.ToArray()
    };
}

/// <summary>Per-user display history and main-window preference, keyed like the whitelist.</summary>
internal sealed class DeviceStateStore
{
    private const int MaximumEntries = 128;
    private const int MaximumPresentationBytes = 64 * 1024;
    private const int MaximumFileBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = [];
    // What each key last queued for writing: its content without the sample time, and that sample time.
    private readonly Dictionary<string, (byte[] Content, DateTimeOffset Sample)> _queued = [];
    private string? _preferredKey;
    private long _version, _savedVersion;
    // True while _entries holds a newer sample than any queued write (only the sample time differs).
    private bool _unsaved;
    private bool _writing;
    private Task _writer = Task.CompletedTask;

    internal DeviceStateStore(string path)
    {
        _path = path; Load();
        lock (_gate) if (_version != _savedVersion) StartWriterLocked();
    }
    internal static DeviceStateStore NextTo(DeviceWhitelistStore whitelist) =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(whitelist.FilePath))!, "device-state.json"));
    internal string? PreferredKey { get { lock (_gate) return _preferredKey; } }
    internal DevicePresentation? Find(string key)
    { lock (_gate) return _entries.GetValueOrDefault(key)?.Presentation; }

    internal bool RememberSelection(string key)
    {
        if (!ValidKey(key)) return false;
        lock (_gate)
        {
            if (_preferredKey == key) return false;
            _preferredKey = key; ChangedLocked(); return true;
        }
    }

    internal void Remember(string key, DevicePresentation presentation)
    {
        presentation = NormalizeAncToggles(presentation);
        if (!ValidKey(key) || !ValidPresentation(presentation)) return;
        // A re-read that changes nothing but the time would rewrite the whole file for a timestamp.
        var content = JsonSerializer.SerializeToUtf8Bytes(presentation with { SampleTime = default }, Json);
        lock (_gate)
        {
            _entries[key] = new(key, presentation);
            if (_queued.TryGetValue(key, out var queued) && queued.Content.AsSpan().SequenceEqual(content)
                && presentation.SampleTime >= queued.Sample && presentation.SampleTime - queued.Sample < TimeSpan.FromSeconds(60))
            { _unsaved = true; return; }
            _queued[key] = (content, presentation.SampleTime);
            foreach (var old in _entries.Values.OrderBy(e => e.Presentation.SampleTime)
                .Take(Math.Max(0, _entries.Count - MaximumEntries)).ToArray()) { _entries.Remove(old.Key); _queued.Remove(old.Key); }
            ChangedLocked();
        }
    }

    // A verified different product retires the display history for that physical peer.
    internal void RetireOtherProducts(string key, string productId)
    {
        lock (_gate)
        {
            var obsolete = _entries.Values.Where(e => e.Key == key && e.Presentation.ProductId != productId).ToArray();
            foreach (var old in obsolete) { _entries.Remove(old.Key); _queued.Remove(old.Key); }
            if (obsolete.Length != 0) ChangedLocked();
        }
    }

    // Any write serializes every entry, so it also carries the samples skipped by Remember.
    private void ChangedLocked()
    { _version++; _unsaved = false; StartWriterLocked(); }
    private void StartWriterLocked()
    { if (!_writing) { _writing = true; _writer = Task.Run(WriteLoopAsync); } }

    // One writer coalesces bursts, never takes the supervisor lock, and is not tied to
    // its cancellation token. Shutdown can therefore drain the final accepted state.
    private async Task WriteLoopAsync()
    {
        while (true)
        {
            await Task.Delay(100);
            Document document; long version;
            lock (_gate)
            {
                if (_version == _savedVersion) { _writing = false; return; }
                version = _version;
                document = new(2, _preferredKey, _entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal).ToArray());
            }
            bool saved = false;
            try { Write(document); saved = true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { DiagnosticLog.Warning("Settings", "deviceState.saveFailed", "Device state cache could not be saved; it will be retried", error, new { path = _path, entries = document.Devices.Length }); }
            lock (_gate)
            {
                if (saved) _savedVersion = version;
                if (!saved || _version == version) { _writing = false; return; }
            }
        }
    }

    internal async Task FlushAsync()
    {
        Task writer;
        lock (_gate)
        {
            if (_unsaved) ChangedLocked();
            else if (_savedVersion != _version) StartWriterLocked();
            writer = _writer;
        }
        await writer;
    }

    private void Load()
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaximumFileBytes) return;
            using var json = JsonDocument.Parse(stream);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version)) return;
            Document? document;
            if (version == 1)
            {
                var legacy = json.RootElement.Deserialize<LegacyDocument>(Json);
                if (legacy?.Devices is not { Length: <= MaximumEntries }) return;
                document = new(2, legacy.PreferredDeviceKey, legacy.Devices
                    .Where(e => e is not null && e.Source == "HeyMelody")
                    .Select(e => new Entry(e.Key, e.Presentation)).ToArray());
            }
            else if (version == 2) document = json.RootElement.Deserialize<Document>(Json);
            else return;
            if (document is not { SchemaVersion: 2, Devices: { Length: <= MaximumEntries } devices }
                || document.PreferredDeviceKey is not null && !ValidKey(document.PreferredDeviceKey)) return;
            _preferredKey = document.PreferredDeviceKey;
            foreach (var entry in devices)
                if (entry is not null && ValidKey(entry.Key))
                {
                    var presentation = NormalizeAncToggles(entry.Presentation);
                    if (!ValidPresentation(presentation)) continue;
                    _entries[entry.Key] = entry with { Presentation = presentation };
                    // The file already holds this sample: seed Remember's throttle so an identical re-read within 60 s of it
                    // is not rewritten. Only when the file needs no migration or repair, so the seed matches what is on disk.
                    if (version != 2) continue;
                    var content = JsonSerializer.SerializeToUtf8Bytes(presentation with { SampleTime = default }, Json);
                    if (content.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(entry.Presentation with { SampleTime = default }, Json)))
                        _queued[entry.Key] = (content, presentation.SampleTime);
                }
            if (version == 1) _version++;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            if (error is FileNotFoundException or DirectoryNotFoundException)
                DiagnosticLog.Debug("Settings", "deviceState.missing", "Settings file does not exist; defaults apply", new { path = _path });
            else
                DiagnosticLog.Warning("Settings", "deviceState.loadFailed", "Settings file could not be read; defaults apply", error, new { path = _path });
        }
    }

    private void Write(Document document)
    {
        string absolutePath = Path.GetFullPath(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        string temporaryPath = absolutePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, document, Json); stream.Flush(flushToDisk: true); }
            File.Move(temporaryPath, absolutePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { DiagnosticLog.Debug("Settings", "deviceState.tempDeleteFailed", "Temporary settings file could not be deleted", new { path = temporaryPath, failure = error.Message }); }
        }
    }

    private static bool ValidKey(string? key) => key is { Length: 64 }
        && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Text(string? value, int limit = 4096) => value is not null && value.Length <= limit;
    private static bool Id(string? value) => value is { Length: > 0 and <= 128 };
    private static bool Options(IReadOnlyList<UiOption>? options) => options is { Count: <= 256 }
        && options.All(o => o is not null && Text(o.Name)) && options.Select(o => o.Key).Distinct().Count() == options.Count;
    private static DevicePresentation NormalizeAncToggles(DevicePresentation presentation)
    {
        if (presentation?.AncModes is null) return presentation!;
        return presentation with
        {
            // Preserve malformed null entries for ValidPresentation to reject; never repair the mode list itself.
            AncModes = presentation.AncModes.Select(mode => mode is null ? null!
                : mode.Toggle is null || ValidAncToggle(mode) ? mode : mode with { Toggle = null }).ToArray()
        };
    }
    private static bool ValidAncToggle(UiAncMode mode) => mode.Toggle is { } toggle
        && Text(toggle.Title) && toggle.Title.Length > 0 && Text(toggle.Description)
        && mode.Key is >= 0 and <= byte.MaxValue && toggle.OffKey != toggle.OnKey
        && toggle.OffKey is >= 0 and <= ushort.MaxValue && toggle.OnKey is >= 0 and <= ushort.MaxValue
        && (toggle.OffKey >> 8) == mode.Key && (toggle.OnKey >> 8) == mode.Key
        && mode.Options is { Count: 2 } options && options.All(option => option is not null)
        && options.Any(option => option.Key == toggle.OffKey) && options.Any(option => option.Key == toggle.OnKey);
    private static bool ValidPresentation(DevicePresentation? p)
    {
        if (p is null || p.ProductId is not { Length: 6 } || !p.ProductId.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F')
            || p.SampleTime == default || p.Firmware is not null && !Text(p.Firmware)
            || p.CustomEqualizerCapacity is < 0 or > byte.MaxValue || p.EqualizerFrequencies is not { Length: <= 64 }
            || p.EqualizerFrequencies.Any(hz => hz is < 1 or > 200000)
            || p.EqualizerFrequencies.Distinct().Count() != p.EqualizerFrequencies.Length
            || !Options(p.AncOptions) || !Options(p.EqualizerOptions) || !Options(p.SpatialOptions)
            || p.AncModes is not { Length: <= 64 } || p.AncModes.Any(m => m is null || !Text(m.Name) || !Text(m.IconKey, 128)
                || !Text(m.Description) || !Options(m.Options) || m.Toggle is not null && !ValidAncToggle(m))
            || p.AncModes.Select(m => m.Key).Distinct().Count() != p.AncModes.Length
            || p.EqualizerEntries is not { Length: <= 256 } || p.EqualizerEntries.Any(e => e is null || !Text(e.Name)
                || e.MinimumGain < sbyte.MinValue || e.MaximumGain > sbyte.MaxValue || e.MinimumGain > e.MaximumGain || e.Bands is not { Count: <= 64 }
                || e.Bands.Any(b => b is null || b.FrequencyHz is < 1 or > 200000 || b.Gain < e.MinimumGain || b.Gain > e.MaximumGain)
                || e.Bands.Select(b => b.FrequencyHz).Distinct().Count() != e.Bands.Count)
            || p.EqualizerEntries.Select(e => e.Id).Distinct().Count() != p.EqualizerEntries.Length
            || p.HearingProfiles is not { Length: <= 128 } || p.HearingProfiles.Any(h => h is null || !Text(h.Name))
            || p.HearingProfiles.Select(h => h.Uid).Distinct().Count() != p.HearingProfiles.Length
            || p.Warnings is not { Length: <= 64 } || p.Warnings.Any(w => !Text(w))
            || p.FeatureSections is not { Length: <= 64 }
            || p.FeatureSections.Any(s => s is null || !Id(s.Id) || !Text(s.Header) || !Text(s.Description)
                || s.Controls is not { Length: <= 256 } || s.Controls.Any(c => c is null || !Id(c.Id) || !Text(c.Label) || !Text(c.Description)
                    || c.Text is not null && !Text(c.Text) || c.Kind is not ("Switch" or "Slider" or "Radio" or "Combo" or "Text")
                    || c.Minimum > c.Maximum || c.Step <= 0 || !Options(c.Options)))
            || p.FeatureSections.Select(s => s.Id).Distinct().Count() != p.FeatureSections.Length) return false;
        var controls = p.FeatureSections.SelectMany(s => s.Controls).ToArray();
        return controls.Length <= 512 && controls.Select(c => c.Id).Distinct().Count() == controls.Length
            && JsonSerializer.SerializeToUtf8Bytes(p, Json).Length <= MaximumPresentationBytes;
    }

    private sealed record Entry(string Key, DevicePresentation Presentation);
    private sealed record Document(int SchemaVersion, string? PreferredDeviceKey, Entry[] Devices);
    // Read-only schema-1 migration: never relabel an unsupported source's presentation.
    private sealed record LegacyEntry(string Key, string Source, DevicePresentation Presentation);
    private sealed record LegacyDocument(int SchemaVersion, string? PreferredDeviceKey, LegacyEntry[] Devices);
}
