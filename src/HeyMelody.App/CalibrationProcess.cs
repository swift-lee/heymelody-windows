using System.Diagnostics;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.App;

/// <summary>Launches an operation window and owns only that child process.</summary>
internal sealed class CalibrationProcess
{
    public async Task RunAsync(string kind, string handle, ICalibrationService service, CancellationToken token)
    {
        var component = kind switch { "fit" => "HeyMelody.FitTest", "hearing" => "HeyMelody.HearingSetup", _ => throw new ArgumentException("Unknown calibration component.", nameof(kind)) };
        var start = new ProcessStartInfo { FileName = Path.Combine(AppContext.BaseDirectory, component + ".exe"),
            WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        if (!File.Exists(start.FileName))
        {
            var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../", component, "bin"));
            var dll = new[] { "Release", "Debug" }.Select(configuration => Path.Combine(directory, configuration, "net10.0", component + ".dll")).FirstOrDefault(File.Exists);
            if (dll is null) throw new FileNotFoundException("无法打开测试窗口，请重启软件或重新安装。");
            start.FileName = "dotnet"; start.ArgumentList.Add(dll);
        }
        start.ArgumentList.Add("--handle"); start.ArgumentList.Add(handle);
        using var process = ClientBridge.StartOwnedComponent(start, token);
        DiagnosticLog.Info("ui.process", "launched", "Launched a calibration window process",
            new { component, kind, start.FileName, args = start.ArgumentList.ToArray(), processId = TryId(process) });
        var errors = DrainAsync(component, process.StandardError);
        try
        {
            await process.WaitForExitAsync(token); await errors;
            DiagnosticLog.Info("ui.process", "exited", "Calibration window process exited", new { component, exitCode = process.ExitCode });
            if (process.ExitCode is not (0 or 1)) throw new IOException("测试窗口未正常完成。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            DiagnosticLog.Info("ui.process", "cancelled", "Calibration window was cancelled; stopping its process", new { component });
            try { await service.CancelCalibrationAsync(handle, CancellationToken.None); }
            catch (Exception error) { DiagnosticLog.Warning("ui.process", "cancelCalibrationFailed", "Could not cancel the calibration after its window was cancelled", error, new { component }); }
            await DeviceManagerProcess.StopAsync(process); await errors; throw;
        }
    }
    private static int? TryId(Process process) { try { return process.Id; } catch (InvalidOperationException) { return null; } }
    private static async Task DrainAsync(string component, StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                DiagnosticLog.Debug("ui.process", "stderr", "Child stderr", new { component, line = line.Length <= 2000 ? line : line[..2000] });
        }
        catch (IOException) { }
    }
}
