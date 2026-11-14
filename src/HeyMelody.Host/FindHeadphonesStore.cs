using System.Text.Json;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Host;

internal sealed record DeviceLocationPreference(bool Enabled, UiRecordedLocation? Location);
internal sealed record FindHeadphonesPreferences(bool Enabled, IReadOnlyDictionary<string, DeviceLocationPreference> Devices);
internal interface IPreparedLocationWrite : IDisposable
{
    void Commit();
}

/// <summary>Local location preferences use the stable hashed device key, never a session identity or Bluetooth address.</summary>
internal class FindHeadphonesStore(string path)
{
    private const int MaximumDevices = 4096;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static FindHeadphonesStore NextTo(DeviceWhitelistStore whitelist) =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(whitelist.FilePath))!, "find-headphones.json"));

    internal FindHeadphonesPreferences Load()
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 2 * 1024 * 1024) throw new InvalidDataException("位置记录文件过大。");
            var document = JsonSerializer.Deserialize<Document>(stream, Json);
            if (document is null || document.SchemaVersion != 1 || document.Devices is null
                || document.Devices.Length > MaximumDevices) throw new InvalidDataException("位置记录文件无效。");
            var devices = new Dictionary<string, DeviceLocationPreference>(StringComparer.Ordinal);
            foreach (var entry in document.Devices)
            {
                if (entry is null || !ValidKey(entry.Key) || !ValidLocation(entry.Location)
                    || !devices.TryAdd(entry.Key, new(entry.Enabled, entry.Location)))
                    throw new InvalidDataException("位置记录文件无效。");
            }
            return new(document.Enabled, devices);
        }
        catch (FileNotFoundException) { return new(false, new Dictionary<string, DeviceLocationPreference>()); }
        catch (DirectoryNotFoundException) { return new(false, new Dictionary<string, DeviceLocationPreference>()); }
        catch (JsonException error) { throw new InvalidDataException("位置记录文件无效。", error); }
    }

    internal virtual Task<IPreparedLocationWrite> PrepareAsync(bool enabled,
        IReadOnlyDictionary<string, DeviceLocationPreference> devices, CancellationToken token) =>
        Task.Run(() => Prepare(enabled, devices, token), token);

    private IPreparedLocationWrite Prepare(bool enabled, IReadOnlyDictionary<string, DeviceLocationPreference> devices,
        CancellationToken token)
    {
        if (devices.Count > MaximumDevices || devices.Any(entry => !ValidKey(entry.Key) || !ValidLocation(entry.Value.Location)))
            throw new InvalidDataException("位置记录无效。");
        string absolutePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        string temporary = absolutePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            token.ThrowIfCancellationRequested();
            var entries = devices.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new Entry(entry.Key, entry.Value.Enabled, entry.Value.Location)).ToArray();
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new Document(1, enabled, entries), Json);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            return new PreparedWrite(absolutePath, temporary);
        }
        catch
        {
            DeleteTemporary(temporary);
            throw;
        }
    }
    private static void DeleteTemporary(string temporary)
    {
        try { File.Delete(temporary); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    private sealed class PreparedWrite(string destination, string temporary) : IPreparedLocationWrite
    {
        private string? _temporary = temporary;
        public void Commit()
        {
            string staged = _temporary ?? throw new InvalidOperationException("位置记录写入已结束。");
            File.Move(staged, destination, overwrite: true);
            _temporary = null;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _temporary, null) is { } staged) DeleteTemporary(staged);
        }
    }

    internal static bool ValidLocation(UiRecordedLocation? value) => value is null ||
        (double.IsFinite(value.Latitude) && value.Latitude is >= -90 and <= 90
        && double.IsFinite(value.Longitude) && value.Longitude is >= -180 and <= 180
        && double.IsFinite(value.AccuracyMeters) && value.AccuracyMeters >= 0
        && value.DisconnectedAt != default && value.PositionTimestamp != default);
    private static bool ValidKey(string key) => key is { Length: 64 }
        && key.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private sealed record Entry(string Key, bool Enabled, UiRecordedLocation? Location);
    private sealed record Document(int SchemaVersion, bool Enabled, Entry[] Devices);
}
