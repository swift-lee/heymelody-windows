using System.Text.Json;

namespace HeyMelody.Diagnostics;

public static class DiagnosticSettingsStore
{
    public static DiagnosticSettings Load()
    {
        using var lease = Lock();
        return Read();
    }

    public static void Save(IEnumerable<string> components, DiagnosticArmIntent intent)
    {
        var selected = DiagnosticComponents.Normalize(components);
        if (intent == DiagnosticArmIntent.ArmNew && selected.Length == 0)
            throw new InvalidOperationException("请至少选择一个需要记录日志的组件。");
        using var lease = Lock();
        var settings = Read();
        settings.SelectedComponents = selected;
        if (intent == DiagnosticArmIntent.ArmNew)
            settings.Pending = new() { Components = selected };
        else if (intent == DiagnosticArmIntent.CancelPending || selected.Length == 0)
            settings.Pending = null;
        else if (settings.Pending is not null)
            settings.Pending.Components = selected;
        AtomicWrite(DiagnosticPaths.SettingsFile, settings);
    }

    // Only the process that owns the existing Host singleton calls this method.
    internal static DiagnosticPendingRequest? Consume()
    {
        using var lease = Lock();
        var settings = Read();
        var pending = settings.Pending;
        if (pending is null) return null;
        settings.Pending = null;
        AtomicWrite(DiagnosticPaths.SettingsFile, settings);
        pending.Components = DiagnosticComponents.Normalize(pending.Components);
        return pending.Components.Length == 0 ? null : pending;
    }

    internal static void AtomicWrite<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static DiagnosticSettings Read()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<DiagnosticSettings>(File.ReadAllText(DiagnosticPaths.SettingsFile));
            if (settings is not { SchemaVersion: 1 }) throw new InvalidDataException("不支持的诊断日志设置版本。");
            settings.SelectedComponents = DiagnosticComponents.Normalize(settings.SelectedComponents ?? []);
            return settings;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        // Invalid configuration must never enable logging or be silently overwritten.
    }

    private static IDisposable Lock()
    {
        var mutex = new Mutex(false, DiagnosticPaths.SettingsMutex);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
        catch (AbandonedMutexException) { acquired = true; }
        catch { mutex.Dispose(); throw; }
        if (!acquired) { mutex.Dispose(); throw new IOException("诊断日志设置正在使用中，请稍后重试。"); }
        return new MutexLease(mutex);
    }
    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
