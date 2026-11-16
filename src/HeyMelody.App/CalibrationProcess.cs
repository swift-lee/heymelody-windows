using System.Diagnostics;
using HeyMelody.Diagnostics;
using HeyMelody.Platform;

namespace HeyMelodyWindows.App;

/// <summary>Launches an operation window and owns only that child process.</summary>
internal sealed class CalibrationProcess
{
    public async Task RunAsync(string kind, string handle, ICalibrationService service, CancellationToken token)
    {
        var component = kind switch { "fit" => "HeyMelody.FitTest", "hearing" => "HeyMelody.HearingSetup", _ => throw new ArgumentException("Unknown calibration component.", nameof(kind)) };
        var start = ApplicationPaths.ResolveComponent(component);
        start.RedirectStandardError = true;
        start.ArgumentList.Add("--handle"); start.ArgumentList.Add(handle);
        using var process = ClientBridge.StartOwnedComponent(start, token);
        DiagnosticLog.Info("ui.process", "launched", "Launched a calibration window process",
            global::HeyMelody.Diagnostics.DiagnosticData.Object(("component", component), ("kind", kind), ("FileName", start.FileName), ("args", start.ArgumentList.ToArray()), ("processId", TryId(process))));
        var errors = DrainAsync(component, process.StandardError);
        try
        {
            await process.WaitForExitAsync(token); await errors;
            DiagnosticLog.Info("ui.process", "exited", "Calibration window process exited", global::HeyMelody.Diagnostics.DiagnosticData.Object(("component", component), ("exitCode", process.ExitCode)));
            if (process.ExitCode is not (0 or 1)) throw new IOException("测试窗口未正常完成。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            DiagnosticLog.Info("ui.process", "cancelled", "Calibration window was cancelled; stopping its process", global::HeyMelody.Diagnostics.DiagnosticData.Object(("component", component)));
            try { await service.CancelCalibrationAsync(handle, CancellationToken.None); }
            catch (Exception error) { DiagnosticLog.Warning("ui.process", "cancelCalibrationFailed", "Could not cancel the calibration after its window was cancelled", error, global::HeyMelody.Diagnostics.DiagnosticData.Object(("component", component))); }
            await DeviceManagerProcess.StopAsync(process); await errors; throw;
        }
    }
    private static int? TryId(Process process) { try { return process.Id; } catch (InvalidOperationException) { return null; } }
    private static async Task DrainAsync(string component, StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                DiagnosticLog.Debug("ui.process", "stderr", "Child stderr", global::HeyMelody.Diagnostics.DiagnosticData.Object(("component", component), ("line", line.Length <= 2000 ? line : line[..2000])));
        }
        catch (IOException) { }
    }
}
