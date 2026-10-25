using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace HeyMelody.Diagnostics;

public static class DiagnosticPaths
{
    // Overrides are internal so normal launches cannot accidentally inherit a developer's test directory.
    internal static string? DataRootOverride { get; set; }
    public static string DataRoot => DataRootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeyMelody");
    public static string LogRoot => Path.Combine(DataRoot, "logs");
    public static string UserSessionKey
    {
        get
        {
            var user = Environment.UserDomainName + "\\" + Environment.UserName;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user))).ToLowerInvariant()[..24];
            return hash + "-" + Process.GetCurrentProcess().SessionId;
        }
    }
    internal static string StateRoot => Path.Combine(DataRoot, "diagnostics", UserSessionKey);
    internal static string SettingsFile => Path.Combine(StateRoot, "settings.json");
    internal static string ReadyFile => Path.Combine(StateRoot, "ready.json");
    internal static string SettingsMutex => (OperatingSystem.IsWindows() ? "Local\\" : "") + "HeyMelody.Diagnostics-" + UserSessionKey;

    public static void OpenLogDirectory(Action<string>? opener = null)
    {
        Directory.CreateDirectory(LogRoot);
        Open(LogRoot, opener);
    }

    /// <summary>Opens one session directory; the path comes from a user-writable descriptor, so only existing folders under the log root qualify.</summary>
    public static void OpenLogDirectory(string sessionDirectory, Action<string>? opener = null)
    {
        var root = Path.GetFullPath(LogRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(sessionDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(root, comparison) || !Directory.Exists(target))
            throw new DirectoryNotFoundException("本轮日志目录不存在或不在日志根目录下：" + sessionDirectory);
        Open(target, opener);
    }

    private static void Open(string directory, Action<string>? opener)
    {
        if (opener is not null) { opener(directory); return; }
        // Shell execution can reuse Explorer and return null even when the directory was opened successfully.
        using var process = Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
    }
}
