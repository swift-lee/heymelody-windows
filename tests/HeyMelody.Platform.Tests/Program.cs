using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using HeyMelody.Platform;

namespace HeyMelody.Platform.Tests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--mutex-child", var mutexName]) return IpcChecks.MutexChild(mutexName);
        if (args is ["--pipe-child", var pipeName]) return await IpcChecks.PipeChildAsync(pipeName);
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("IPC paths use UTF-8 byte limits and deterministic fallbacks", Sync(PathChecks.UnixPipePaths)),
            ("private IPC directories are created with mode 0700", Sync(PathChecks.PrivateDirectory)),
            ("unsafe directory modes are rejected without repair", Sync(PathChecks.UnsafeDirectoryMode)),
            ("existing and dangling directory symlinks are rejected", Sync(PathChecks.DirectorySymlinks)),
            ("singleton is shared across real processes and Unix sessions", Sync(IpcChecks.CrossSessionMutex)),
            ("absolute CurrentUserOnly pipe works with different TMPDIR values", IpcChecks.AbsolutePipeAsync),
            ("renamed app bundles resolve nested helpers and shared resources", Sync(PathChecks.RenamedBundle)),
            ("source fallback resolves configuration and RID outputs", Sync(PathChecks.SourceFallback)),
        };
        int passed = 0, skipped = 0, failed = 0;
        foreach (var (name, test) in tests)
        {
            try { await test(); Console.WriteLine("PASS " + name); passed++; }
            catch (SkipException error) { Console.WriteLine("SKIP " + name + ": " + error.Message); skipped++; }
            catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); failed++; }
        }
        Console.WriteLine($"Platform checks: {passed} passed, {skipped} skipped, {failed} failed; no Bluetooth or UI operations.");
        return failed == 0 ? 0 : 1;
    }

    private static Func<Task> Sync(Action action) => () => { action(); return Task.CompletedTask; };
    internal static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    internal static void ThrowsIOException(Action action)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new InvalidOperationException("Expected IOException was not raised.");
    }

    internal static ProcessStartInfo ChildStart(string command, string value, bool newSession, string temporaryDirectory,
        bool inheritTemporaryDirectory = false)
    {
        var executable = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) executable = Environment.ProcessPath!;
        var childArguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            childArguments.Add(Assembly.GetExecutingAssembly().Location);
        childArguments.Add(command); childArguments.Add(value);
        var start = new ProcessStartInfo(newSession ? "python3" : executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = temporaryDirectory, CreateNoWindow = true
        };
        if (newSession)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("import os,sys; os.setsid(); os.execv(sys.argv[1],sys.argv[1:])");
            start.ArgumentList.Add(executable);
        }
        foreach (var argument in childArguments) start.ArgumentList.Add(argument);
        if (!inheritTemporaryDirectory)
            start.Environment["TMPDIR"] = temporaryDirectory + Path.DirectorySeparatorChar;
        return start;
    }

    internal static JsonDocument ReadChild(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new IOException("Could not start the child check.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { process.Kill(); throw new TimeoutException("Child process check timed out."); }
        var text = output.GetAwaiter().GetResult(); var stderr = errors.GetAwaiter().GetResult();
        Require(process.ExitCode == 0, $"Child check exited {process.ExitCode}: {stderr}");
        return JsonDocument.Parse(text);
    }

    internal static int SessionId => OperatingSystem.IsMacOS() ? MacGetSessionId(0) : LinuxGetSessionId(0);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getsid")]
    private static extern int MacGetSessionId(int pid);
    [DllImport("libc", EntryPoint = "getsid")]
    private static extern int LinuxGetSessionId(int pid);
}

internal sealed class SkipException(string message) : Exception(message);

internal sealed class TestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "hm-platform-" + Guid.NewGuid().ToString("N")[..10]);
    public TestDirectory()
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Root);
        else Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public string DirectoryAt(string relative)
    {
        var path = Path.Combine(Root, relative);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
    public static void FileAt(string path)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture"); }
    public void Dispose() { Directory.Delete(Root, recursive: true); }
}
