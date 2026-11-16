using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeyMelodyWindows.Protocol;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Calibration;

public sealed record CalibrationHistoryEntry(string StableKey, int Uid, string Name, HearingVersion Version,
    IReadOnlyList<HearingLevel> Levels, byte[]? ScanData, DateTimeOffset SavedAtUtc,
    byte? RestoreDescriptionId = null, string? WireName = null);

/// <summary>Plain JSON; stable device identity and signed UID are retained without phone account identifiers.</summary>
public sealed class CalibrationHistoryStore(string directory)
{
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<IReadOnlyList<CalibrationHistoryEntry>> ReadAsync(string stableKey, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { var entries = await ReadCoreAsync(stableKey, token); DiagnosticLog.Debug("Calibration.History", "history.read", "Read calibration history.", global::HeyMelody.Diagnostics.DiagnosticData.Object(("stableKey", stableKey), ("directory", _directory), ("entries", DiagnosticData.From(entries, CalibrationJsonContext.Default.HistoryEntries)))); return entries; }
        catch (Exception error) { DiagnosticLog.Error("Calibration.History", "history.read-failed", "Calibration history read failed.", error, global::HeyMelody.Diagnostics.DiagnosticData.Object(("stableKey", stableKey), ("directory", _directory))); throw; }
        finally { _gate.Release(); }
    }
    public async Task CheckSaveAsync(CalibrationHistoryEntry entry, int? replacementUid = null, CancellationToken token = default)
    {
        Validate(entry); await _gate.WaitAsync(token);
        try { _ = Prepare(await ReadCoreAsync(entry.StableKey, token), entry, replacementUid); }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(CalibrationHistoryEntry entry, CancellationToken token = default, int? replacementUid = null)
    {
        DiagnosticLog.Debug("Calibration.History", "history.save-requested", "Saving a calibration profile history entry.", global::HeyMelody.Diagnostics.DiagnosticData.Object(("entry", DiagnosticData.From(entry, CalibrationJsonContext.Default.CalibrationHistoryEntry)), ("replacementUid", replacementUid), ("directory", _directory)));
        Validate(entry); await _gate.WaitAsync(token);
        try
        {
            var entries = Prepare(await ReadCoreAsync(entry.StableKey, token), entry, replacementUid);
            Directory.CreateDirectory(_directory); string path = GetPath(entry.StableKey), temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entries, CalibrationJsonContext.Default.CalibrationHistoryEntryArray), new UTF8Encoding(false), token);
            File.Move(temporary, path, true);
            DiagnosticLog.Info("Calibration.History", "history.saved", "Calibration history was saved.", global::HeyMelody.Diagnostics.DiagnosticData.Object(("StableKey", entry.StableKey), ("Uid", entry.Uid), ("replacementUid", replacementUid), ("path", path)));
        }
        catch (Exception error) { DiagnosticLog.Error("Calibration.History", "history.save-failed", "Calibration history save failed.", error, global::HeyMelody.Diagnostics.DiagnosticData.Object(("StableKey", entry.StableKey), ("Uid", entry.Uid), ("replacementUid", replacementUid), ("directory", _directory))); throw; }
        finally { _gate.Release(); }
    }
    private static CalibrationHistoryEntry[] Prepare(IReadOnlyList<CalibrationHistoryEntry> existing, CalibrationHistoryEntry entry, int? replacementUid)
    {
        if (replacementUid is int replace && !existing.Any(x => x.Uid == replace)) throw new InvalidOperationException("所选的替换方案已不存在。");
        var retained = existing.Where(x => x.Uid != entry.Uid && x.Uid != replacementUid).ToArray();
        if (retained.Any(x => string.Equals(x.Name, entry.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("已有同名方案，请使用其他名称。");
        if (retained.Length >= 10) throw new InvalidOperationException("方案历史已满，请选择一条要替换的记录。");
        return retained.Append(entry).OrderByDescending(x => x.SavedAtUtc).ToArray();
    }
    public async Task DeleteAsync(string stableKey, int uid, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var entries = (await ReadCoreAsync(stableKey, token)).Where(x => x.Uid != uid).ToArray();
            if (!File.Exists(GetPath(stableKey))) return;
            string path = GetPath(stableKey), temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entries, CalibrationJsonContext.Default.CalibrationHistoryEntryArray), new UTF8Encoding(false), token);
            File.Move(temporary, path, true);
            DiagnosticLog.Info("Calibration.History", "history.deleted", "Deleted the requested calibration history entry.", global::HeyMelody.Diagnostics.DiagnosticData.Object(("stableKey", stableKey), ("uid", uid), ("path", path)));
        }
        catch (Exception error) { DiagnosticLog.Error("Calibration.History", "history.delete-failed", "Calibration history delete failed.", error, global::HeyMelody.Diagnostics.DiagnosticData.Object(("stableKey", stableKey), ("uid", uid), ("directory", _directory))); throw; }
        finally { _gate.Release(); }
    }
    private async Task<IReadOnlyList<CalibrationHistoryEntry>> ReadCoreAsync(string stableKey, CancellationToken token)
    {
        string path = GetPath(stableKey); if (!File.Exists(path)) return [];
        var entries = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, token), CalibrationJsonContext.Default.CalibrationHistoryEntryArray) ?? [];
        if (entries.Length > 10 || entries.Any(x => x.StableKey != stableKey) || entries.Select(x => x.Uid).Distinct().Count() != entries.Length)
            throw new InvalidDataException("方案历史数据无效。");
        foreach (var entry in entries) Validate(entry);
        return entries.OrderByDescending(x => x.SavedAtUtc).ToArray();
    }
    private string GetPath(string stableKey)
    {
        if (string.IsNullOrWhiteSpace(stableKey)) throw new ArgumentException("缺少耳机设备标识。", nameof(stableKey));
        return Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableKey))) + ".json");
    }
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length is < 1 or > 40 || name.Any(char.IsControl)) throw new ArgumentException("名称不能为空，且最多40个字符。", nameof(name));
    }
    private static void Validate(CalibrationHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.StableKey) || entry.Version is not (HearingVersion.V1 or HearingVersion.V2)) throw new InvalidDataException("方案历史数据无效。");
        ValidateName(entry.Name); CalibrationCodecs.ValidateLevels(entry.Levels);
        if (entry.Levels.Count != 12 || entry.ScanData?.Length is 0 or > 32767) throw new InvalidDataException("方案历史数据不完整。");
        if (entry.RestoreDescriptionId is 0 or > 20) throw new InvalidDataException("方案描述编号无效。");
        if (entry.WireName is not null) CalibrationCodecs.ValidateName(entry.WireName);
    }
}
