using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HeyMelody.Platform;

/// <summary>Stable user identities, secure short IPC endpoints, and desktop singleton scopes.</summary>
public static class DesktopRuntime
{
    private static readonly Lazy<string> Endpoint = new(() => OperatingSystem.IsWindows()
        ? "HeyMelody-v2-" + UserSessionKey : PrepareUnixPipePath());

    public static string UserSessionKey
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return "uid-" + CurrentUserId.ToString(CultureInfo.InvariantCulture);
            var user = Environment.UserDomainName + "\\" + Environment.UserName;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user))).ToLowerInvariant()[..24];
            return hash + "-" + Process.GetCurrentProcess().SessionId;
        }
    }

    public static string PipeName => Endpoint.Value;
    public static string HostMutexName => MutexName("Host");
    public static string MainMutexName => MutexName("Main");
    public static string MutexName(string role) => (OperatingSystem.IsWindows() ? "Local\\" : "") + "HeyMelody." + role + "-" + UserSessionKey;

    public static Mutex CreateMutex(string name, out bool createdNew) => OperatingSystem.IsWindows()
        ? new Mutex(false, name, out createdNew)
        : new Mutex(false, name, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false }, out createdNew);

    public static bool TryOpenMutex(string name, out Mutex? mutex) => OperatingSystem.IsWindows()
        ? Mutex.TryOpenExisting(name, out mutex)
        : Mutex.TryOpenExisting(name, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false }, out mutex);

    public static uint CurrentUserId => OperatingSystem.IsMacOS() ? MacGetEffectiveUserId()
        : OperatingSystem.IsLinux() ? LinuxGetEffectiveUserId() : throw new PlatformNotSupportedException();

    internal static string GetUnixPipePath(uint uid, bool macOS, string? runtimeDirectory)
    {
        var fallback = Path.Combine(macOS ? "/private/tmp" : "/tmp", "heymelody-" + uid.ToString(CultureInfo.InvariantCulture));
        var directory = !macOS && !string.IsNullOrWhiteSpace(runtimeDirectory) && Path.IsPathFullyQualified(runtimeDirectory)
            ? Path.Combine(runtimeDirectory, "heymelody") : fallback;
        var pipe = Path.Combine(directory, "backend-v2.sock");
        if (Encoding.UTF8.GetByteCount(pipe) > 100) pipe = Path.Combine(fallback, "backend-v2.sock");
        if (Encoding.UTF8.GetByteCount(pipe) > 100) throw new IOException("后台通信路径过长。");
        return pipe;
    }

    private static string PrepareUnixPipePath()
    {
        var macOS = OperatingSystem.IsMacOS();
        if (!macOS && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("当前系统尚无后台通信实现。");
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var pipe = GetUnixPipePath(CurrentUserId, macOS, runtime);
        var directory = Path.GetDirectoryName(pipe)!;
        if (!macOS && !string.IsNullOrWhiteSpace(runtime) && directory == Path.Combine(runtime, "heymelody"))
            ValidateUnixDirectory(runtime, create: false);
        ValidateUnixDirectory(directory, create: true);
        return pipe;
    }

    internal static void ValidateUnixDirectory(string directory, bool create)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (new DirectoryInfo(directory).LinkTarget is not null)
            throw new IOException("后台通信目录不能是符号链接。");
        if (create && !Directory.Exists(directory)) Directory.CreateDirectory(directory, privateMode);
        var info = new DirectoryInfo(directory);
        if (!info.Exists || info.LinkTarget is not null || File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("后台通信目录无效或包含符号链接。");
        if (File.GetUnixFileMode(directory) != privateMode || ReadDirectoryOwner(directory) != CurrentUserId)
            throw new IOException("后台通信目录必须仅供当前用户访问。");
    }

    private static uint ReadDirectoryOwner(string directory)
    {
        // stat's public command interface avoids depending on Darwin/Linux's different native struct layouts.
        var start = new ProcessStartInfo("/usr/bin/stat") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        if (OperatingSystem.IsMacOS()) { start.ArgumentList.Add("-f"); start.ArgumentList.Add("%u"); }
        else { start.ArgumentList.Add("-c"); start.ArgumentList.Add("%u"); start.ArgumentList.Add("--"); }
        start.ArgumentList.Add(directory);
        using var process = Process.Start(start) ?? throw new IOException("无法核验后台通信目录。");
        if (!process.WaitForExit(3000)) { process.Kill(); throw new IOException("后台通信目录核验超时。"); }
        var output = process.StandardOutput.ReadToEnd();
        if (process.ExitCode != 0 || !uint.TryParse(output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var uid))
            throw new IOException("无法核验后台通信目录所有者。");
        return uid;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "geteuid")]
    private static extern uint MacGetEffectiveUserId();
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint LinuxGetEffectiveUserId();
}
