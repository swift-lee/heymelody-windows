using System.Diagnostics;
using HeyMelodyWindows.App;

internal static class ProcessLifetimeTests
{
    private const string EditorMode = "--lifetime-test-editor";
    private const string BackendMode = "--lifetime-test-backend";

    public static bool TryRunHelper(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args is not [EditorMode] and not [BackendMode]) return false;
        if (args[0] == EditorMode)
        {
            using var backend = Process.Start(StartInfo(BackendMode))
                ?? throw new IOException("Unable to start the isolated backend fixture.");
            Console.WriteLine(backend.Id); Console.Out.Flush();
        }
        // A watchdog bounds orphan lifetime even if the parent test crashes before cleanup.
        Thread.Sleep(TimeSpan.FromSeconds(30));
        return true;
    }

    public static async Task RunAsync()
    {
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var prepared = new ProcessStartInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe"));
            try
            {
                using var unexpected = DeviceManagerProcess.StartPreparedProcess(prepared, cancelled.Token);
                throw new InvalidOperationException("A cancelled launch must not start a prepared child process.");
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        }
        using var editor = Process.Start(StartInfo(EditorMode))
            ?? throw new IOException("Unable to start the isolated editor fixture.");
        Process? backend = null;
        try
        {
            var line = await editor.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (!int.TryParse(line, out var backendId)) throw new InvalidDataException("Editor did not identify its test child.");
            backend = Process.GetProcessById(backendId);
            if (editor.HasExited || backend.HasExited) throw new InvalidOperationException("Both test fixtures must initially be running.");
            // This console-only fixture has no window, so the real close path must fall back to forced termination.
            await DeviceManagerProcess.StopAsync(editor).WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(250);
            backend.Refresh();
            if (!editor.HasExited || backend.HasExited)
                throw new InvalidOperationException("Terminating the owned editor must leave its separately running backend child alive.");
        }
        finally
        {
            await CleanupAsync(editor);
            if (backend is not null) { await CleanupAsync(backend); backend.Dispose(); }
        }
    }

    private static ProcessStartInfo StartInfo(string mode)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable missing.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(ProcessLifetimeTests).Assembly.Location);
        start.ArgumentList.Add(mode);
        return start;
    }

    private static async Task CleanupAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (InvalidOperationException) { }
    }
}
