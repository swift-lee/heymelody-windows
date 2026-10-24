using System.Diagnostics;

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
        var errors = DrainAsync(process.StandardError);
        try { await process.WaitForExitAsync(token); await errors; if (process.ExitCode is not (0 or 1)) throw new IOException("测试窗口未正常完成。"); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            try { await service.CancelCalibrationAsync(handle, CancellationToken.None); } catch { }
            await DeviceManagerProcess.StopAsync(process); await errors; throw;
        }
    }
    private static async Task DrainAsync(StreamReader reader)
    { var buffer = new char[1024]; try { while (await reader.ReadAsync(buffer) != 0) { } } catch (IOException) { } }
}
