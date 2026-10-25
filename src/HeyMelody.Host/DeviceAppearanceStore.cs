using System.Text.Json;
using HeyMelody.Diagnostics;
using System.Text.RegularExpressions;

namespace HeyMelodyWindows.Host;

/// <summary>A product ID and protocol color once observed for a whitelisted device.</summary>
internal sealed record DeviceAppearance(string ProductId, byte? Color, bool ColorFromSpp);

/// <summary>
/// Per-user appearance hints keyed like the whitelist, so artwork can be shown before a control
/// session exists. Hints are display-only: they never grant ControlReady, capabilities or settings.
/// Addresses and session identities never enter the file.
/// </summary>
internal sealed partial class DeviceAppearanceStore(string path)
{
    private const int MaximumDevices = 4096;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _gate = new();
    private Dictionary<string, Entry>? _entries;

    public static DeviceAppearanceStore NextTo(DeviceWhitelistStore whitelist) =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(whitelist.FilePath))!, "device-appearance.json"));

    public DeviceAppearance? Find(string key)
    {
        lock (_gate)
            return Load().TryGetValue(key, out var entry) ? new(entry.ProductId, entry.Color, entry.ColorSource == "spp") : null;
    }

    /// <summary>Stores a changed hint and reports whether the stored value changed. Save failures keep the memory value.</summary>
    public bool Save(string key, DeviceAppearance appearance)
    {
        if (!IsValidKey(key) || !ProductPattern().IsMatch(appearance.ProductId)) return false;
        lock (_gate)
        {
            var entries = Load();
            var next = new Entry(key, appearance.ProductId, appearance.Color,
                appearance.Color is null ? null : appearance.ColorFromSpp ? "spp" : "ble", DateTimeOffset.UtcNow);
            if (entries.TryGetValue(key, out var old) && old.ProductId == next.ProductId && old.Color == next.Color
                && old.ColorSource == next.ColorSource) return false;
            entries[key] = next;
            foreach (var stale in entries.Values.OrderBy(entry => entry.UpdatedAt).Take(Math.Max(0, entries.Count - MaximumDevices)).ToArray())
                entries.Remove(stale.Key);
            try { Write(entries.Values.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray()); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { DiagnosticLog.Warning("Settings", "appearance.saveFailed", "Device appearance cache could not be saved", error, new { path }); }
            return true;
        }
    }

    private Dictionary<string, Entry> Load()
    {
        if (_entries is not null) return _entries;
        _entries = new(StringComparer.Ordinal);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 4 * 1024 * 1024) return _entries;
            var document = JsonSerializer.Deserialize<Document>(stream, Json);
            if (document is not { SchemaVersion: 1, Devices: { } devices }) return _entries;
            foreach (var entry in devices.Take(MaximumDevices))
                if (IsValidKey(entry.Key) && ProductPattern().IsMatch(entry.ProductId ?? "")
                    && entry.ColorSource is null or "spp" or "ble" && (entry.Color is null) == (entry.ColorSource is null))
                    _entries[entry.Key] = entry;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            if (error is FileNotFoundException or DirectoryNotFoundException)
                DiagnosticLog.Debug("Settings", "appearance.missing", "Settings file does not exist; defaults apply", new { path = path });
            else
                DiagnosticLog.Warning("Settings", "appearance.loadFailed", "Settings file could not be read; defaults apply", error, new { path = path });
        }
        // A damaged or missing file is only a cache; the next observation rewrites it.
        return _entries;
    }

    private void Write(Entry[] entries)
    {
        string absolutePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        string temporaryPath = absolutePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new Document(1, entries), Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, absolutePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { DiagnosticLog.Debug("Settings", "appearance.tempDeleteFailed", "Temporary settings file could not be deleted", new { path = temporaryPath, failure = error.Message }); }
        }
    }

    private static bool IsValidKey(string? key) => key is { Length: 64 }
        && key.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    [GeneratedRegex("^[0-9A-F]{6}$")]
    private static partial Regex ProductPattern();

    private sealed record Entry(string Key, string ProductId, byte? Color, string? ColorSource, DateTimeOffset UpdatedAt);
    private sealed record Document(int SchemaVersion, Entry[] Devices);
}
