using System.Diagnostics;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.App;

internal enum DeviceManagerOutcome { Saved, Cancelled }

internal interface IDeviceManagerProcess
{
    Task<DeviceManagerOutcome> RunAsync(CancellationToken token);
}

internal sealed class TrackedDeviceManager(IDeviceManagerProcess manager) : IDeviceManagerProcess
{
    public Task<DeviceManagerOutcome>? CurrentOperation { get; private set; }
    public async Task<DeviceManagerOutcome> RunAsync(CancellationToken token)
    {
        var operation = manager.RunAsync(token);
        CurrentOperation = operation;
        try { return await operation; }
        finally { if (ReferenceEquals(CurrentOperation, operation)) CurrentOperation = null; }
    }
}

/// <summary>Owns only the device editor launched by this application.</summary>
internal sealed class DeviceManagerProcess : IDeviceManagerProcess
{
    public async Task<DeviceManagerOutcome> RunAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "HeyMelody.DeviceManager.exe"),
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        if (!File.Exists(start.FileName))
        {
            var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../HeyMelody.DeviceManager/bin"));
            var dll = new[] { "Release", "Debug" }.Select(configuration =>
                Path.Combine(project, configuration, "net10.0", "HeyMelody.DeviceManager.dll"))
                .FirstOrDefault(File.Exists);
            if (dll is null) throw new FileNotFoundException("设备管理组件缺失，请重启软件或重新安装。");
            start.FileName = "dotnet";
            start.ArgumentList.Add(dll);
        }
        using var process = StartPreparedProcess(start, token);
        DiagnosticLog.Info("ui.process", "launched", "Launched the device manager process",
            new { component = "HeyMelody.DeviceManager", start.FileName, args = start.ArgumentList.ToArray(), processId = TryId(process) });
        var errors = DrainAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(token);
            await errors;
            DiagnosticLog.Info("ui.process", "exited", "Device manager process exited", new { component = "HeyMelody.DeviceManager", exitCode = process.ExitCode });
            return process.ExitCode switch
            {
                0 => DeviceManagerOutcome.Saved,
                1 => DeviceManagerOutcome.Cancelled,
                _ => throw new IOException("设备管理组件未正常完成，请重试。")
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            DiagnosticLog.Info("ui.process", "cancelled", "Device manager was cancelled; stopping its process");
            await StopAsync(process);
            await errors;
            throw;
        }
    }

    internal static Process StartPreparedProcess(ProcessStartInfo start, CancellationToken token)
        => ClientBridge.StartOwnedComponent(start, token);

    private static int? TryId(Process process) { try { return process.Id; } catch (InvalidOperationException) { return null; } }
    private static async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                DiagnosticLog.Debug("ui.process", "stderr", "Child stderr", new { component = "HeyMelody.DeviceManager", line = line.Length <= 2000 ? line : line[..2000] });
        }
        catch (IOException) { }
    }

    internal static async Task StopAsync(Process process)
    {
        if (process.HasExited) return;
        try
        {
            if (process.CloseMainWindow())
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); return; }
                catch (TimeoutException) { }
            }
            // A reconnect may have launched the shared Host from the editor; only the editor is owned here.
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException error)
        { DiagnosticLog.Debug("ui.process", "stopRaced", "The component process ended while it was being stopped", new { error = error.Message }); }
    }
}
