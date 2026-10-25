using System.Diagnostics;

namespace HeyMelody.Platform;

/// <summary>Locations shared by the desktop components, independent of a particular apphost.</summary>
public static class ApplicationPaths
{
    public static string DataRoot => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "HeyMelody")
        : OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeyMelody")
            : Path.Combine(GetXdgDataRoot(), "HeyMelody");

    public static string ResourceRoot => GetResourceRoot(AppContext.BaseDirectory);

    public static string GetResourceRoot(string baseDirectory)
    {
        var bundle = FindApplicationBundle(baseDirectory);
        if (bundle is not null) return Path.Combine(bundle, "Contents", "Resources");
        foreach (var directory in Ancestors(baseDirectory))
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "ResourceBundles"))) return directory.FullName;
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "HeyMelody.Resources"))) return directory.FullName;
        }
        return baseDirectory;
    }

    public static string? FindApplicationBundle(string baseDirectory)
    {
        string? candidate = null;
        foreach (var directory in Ancestors(baseDirectory))
        {
            if (!directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) continue;
            candidate = directory.FullName;
            if (Directory.Exists(Path.Combine(candidate, "Contents", "Helpers"))) return candidate;
        }
        return candidate;
    }

    /// <summary>Resolve a component in a bundle, a loose publish directory, or a source checkout.</summary>
    public static ProcessStartInfo ResolveComponent(string assemblyName, string? baseDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        if (assemblyName == "HeyMelody.App") assemblyName = "HeyMelody";
        if (assemblyName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("A component assembly name is required.", nameof(assemblyName));
        var executableName = assemblyName + (OperatingSystem.IsWindows() ? ".exe" : "");
        var bundle = FindApplicationBundle(baseDirectory);
        if (bundle is not null)
        {
            var component = assemblyName == "HeyMelody" ? bundle
                : Path.Combine(bundle, "Contents", "Helpers", assemblyName + ".app");
            var executable = Path.Combine(component, "Contents", "MacOS", executableName);
            if (File.Exists(executable)) return StartInfo(executable, Path.GetDirectoryName(executable)!);
            throw new FileNotFoundException("应用组件缺失，请重新安装完整客户端。", executable);
        }

        foreach (var directory in new[] { baseDirectory, Path.Combine(baseDirectory, "backend"), Path.GetFullPath(Path.Combine(baseDirectory, "..")) })
        {
            var executable = Path.Combine(directory, executableName);
            if (File.Exists(executable)) return StartInfo(executable, directory);
            var dll = Path.Combine(directory, assemblyName + ".dll");
            if (File.Exists(dll)) return DotnetStart(dll);
        }

        var project = assemblyName == "HeyMelody" ? "HeyMelody.App" : assemblyName;
        foreach (var directory in Ancestors(baseDirectory))
        {
            var source = Directory.Exists(Path.Combine(directory.FullName, "src")) ? Path.Combine(directory.FullName, "src")
                : directory.Name == "src" ? directory.FullName : null;
            if (source is null) continue;
            var bin = Path.Combine(source, project, "bin");
            if (!Directory.Exists(bin)) continue;
            var preferred = baseDirectory.Split(Path.DirectorySeparatorChar).Contains("Release") ? "Release" : "Debug";
            foreach (var configuration in new[] { preferred, preferred == "Debug" ? "Release" : "Debug" })
            {
                var output = Path.Combine(bin, configuration);
                if (!Directory.Exists(output)) continue;
                var frameworks = Directory.EnumerateDirectories(output, "net10.0*")
                    .Where(p => OperatingSystem.IsWindows() ? Path.GetFileName(p).Contains("windows") || Path.GetFileName(p) == "net10.0"
                        : Path.GetFileName(p) == "net10.0").OrderBy(p => p, StringComparer.Ordinal);
                foreach (var framework in frameworks)
                {
                    var dll = Path.Combine(framework, assemblyName + ".dll");
                    if (File.Exists(dll)) return DotnetStart(dll);
                    var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
                    dll = Path.Combine(framework, rid, assemblyName + ".dll");
                    if (File.Exists(dll)) return DotnetStart(dll);
                }
            }
        }
        throw new FileNotFoundException("应用组件缺失，请先构建组件或重新安装完整客户端。", executableName);
    }

    private static ProcessStartInfo StartInfo(string executable, string directory) => new()
    { FileName = executable, WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };

    private static ProcessStartInfo DotnetStart(string dll)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host) || !File.Exists(host)) host = "dotnet";
        var start = StartInfo(host, Path.GetDirectoryName(dll)!);
        start.ArgumentList.Add(dll);
        return start;
    }

    private static string GetXdgDataRoot()
    {
        var value = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) ? value
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    }

    private static IEnumerable<DirectoryInfo> Ancestors(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
            yield return directory;
    }
}
