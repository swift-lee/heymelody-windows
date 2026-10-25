using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Host;

/// <summary>Per-user allowed Classic devices. Addresses and session identities never enter the file.</summary>
internal sealed class DeviceWhitelistStore(string path)
{
    private const int MaximumDevices = 4096;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal string FilePath => path;

    public static DeviceWhitelistStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeyMelody", "devices.json"));

    internal static string KeyFor(ulong address)
    {
        if (address == 0 || address > 0xFFFFFFFFFFFFUL) throw new ArgumentOutOfRangeException(nameof(address));
        var source = "HeyMelody/BluetoothClassic/v1/" + address.ToString("X12", CultureInfo.InvariantCulture);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(source)));
    }

    public HashSet<string> Load()
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 1024 * 1024) throw new InvalidDataException("设备白名单文件过大。请重新选择设备。");
            var document = JsonSerializer.Deserialize<WhitelistDocument>(stream, Json);
            if (document is null || document.SchemaVersion != 1 || document.DeviceKeys is null
                || document.DeviceKeys.Length > MaximumDevices || document.DeviceKeys.Any(key => !IsValidKey(key)))
                throw new InvalidDataException("设备白名单文件无效。请重新选择设备。");
            return new(document.DeviceKeys, StringComparer.Ordinal);
        }
        catch (FileNotFoundException) { return new(StringComparer.Ordinal); }
        catch (DirectoryNotFoundException) { return new(StringComparer.Ordinal); }
        catch (JsonException error)
        {
            DiagnosticLog.Warning("Settings", "whitelist.loadFailed", "Device whitelist file is not valid JSON", error, new { path });
            throw new InvalidDataException("设备白名单文件无效。请重新选择设备。", error);
        }
    }

    public void Save(IEnumerable<string> keys)
    {
        var allowed = keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (allowed.Length is 0 or > MaximumDevices || allowed.Any(key => !IsValidKey(key)))
            throw new InvalidDataException("请至少选择一台有效的音频设备。");

        string absolutePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        string temporaryPath = absolutePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new WhitelistDocument(1, allowed), Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, absolutePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { DiagnosticLog.Debug("Settings", "whitelist.tempDeleteFailed", "Temporary whitelist file could not be deleted", new { path = temporaryPath, failure = error.Message }); }
        }
    }

    private static bool IsValidKey(string? key) => key is { Length: 64 }
        && key.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record WhitelistDocument(int SchemaVersion, string[] DeviceKeys);
}
