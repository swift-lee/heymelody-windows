using System.Runtime.InteropServices;
using System.Text;
using HeyMelody.Platform;

namespace HeyMelody.Platform.Tests;

internal static class PathChecks
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    public static void UnixPipePaths()
    {
        if (OperatingSystem.IsWindows()) throw new SkipException("Unix endpoint path policy.");
        const uint uid = 12345;
        var fallback = DesktopRuntime.GetUnixPipePath(uid, macOS: false, null);
        foreach (var runtime in new[] { "/tmp/" + new string('a', 120), "/tmp/" + new string('界', 30), "relative/path" })
        {
            var result = DesktopRuntime.GetUnixPipePath(uid, macOS: false, runtime);
            Program.Require(result == fallback && Encoding.UTF8.GetByteCount(result) <= 100,
                "An overlong or relative XDG path did not use the short fallback.");
        }
        var prefix = "/tmp/";
        int fixedBytes = Encoding.UTF8.GetByteCount(Path.Combine(prefix + "x", "heymelody", "backend-v2.sock")) - 1;
        var boundary = prefix + new string('a', 100 - fixedBytes);
        var exact = DesktopRuntime.GetUnixPipePath(uid, macOS: false, boundary);
        Program.Require(Encoding.UTF8.GetByteCount(exact) == 100 && exact.StartsWith(boundary, StringComparison.Ordinal),
            "An exactly 100-byte endpoint should remain usable.");
        var mac = DesktopRuntime.GetUnixPipePath(uid, macOS: true, "/different/runtime");
        Program.Require(mac.StartsWith("/private/tmp/heymelody-12345/", StringComparison.Ordinal), "macOS endpoint changed with XDG_RUNTIME_DIR.");
    }

    public static void PrivateDirectory()
    {
        if (OperatingSystem.IsWindows()) throw new SkipException("Unix directory permissions.");
        using var scope = new TestDirectory(); var path = Path.Combine(scope.Root, "ipc");
        DesktopRuntime.ValidateUnixDirectory(path, create: true);
        Program.Require(File.GetUnixFileMode(path) == Private, "New IPC directory does not have mode 0700.");
        DesktopRuntime.ValidateUnixDirectory(path, create: false);
    }

    public static void UnsafeDirectoryMode()
    {
        if (OperatingSystem.IsWindows()) throw new SkipException("Unix directory permissions.");
        using var scope = new TestDirectory(); var path = scope.DirectoryAt("unsafe");
        var unsafeMode = Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(path, unsafeMode);
        Program.ThrowsIOException(() => DesktopRuntime.ValidateUnixDirectory(path, create: true));
        Program.Require(File.GetUnixFileMode(path) == unsafeMode, "Validation silently repaired an existing unsafe directory.");
    }

    public static void DirectorySymlinks()
    {
        if (OperatingSystem.IsWindows()) throw new SkipException("Unix directory symlinks.");
        using var scope = new TestDirectory(); var target = scope.DirectoryAt("target");
        var existing = Path.Combine(scope.Root, "linked"); Directory.CreateSymbolicLink(existing, target);
        Program.ThrowsIOException(() => DesktopRuntime.ValidateUnixDirectory(existing, create: true));
        var absent = Path.Combine(scope.Root, "absent"); var dangling = Path.Combine(scope.Root, "dangling");
        Directory.CreateSymbolicLink(dangling, absent);
        Program.ThrowsIOException(() => DesktopRuntime.ValidateUnixDirectory(dangling, create: true));
        Program.Require(!Directory.Exists(absent), "Validation followed a dangling symlink and created its target.");
    }

    public static void RenamedBundle()
    {
        using var scope = new TestDirectory(); var original = scope.DirectoryAt("Original Name.app");
        Directory.CreateDirectory(Path.Combine(original, "Contents/Helpers"));
        Directory.CreateDirectory(Path.Combine(original, "Contents/Resources/ResourceBundles"));
        string[] components = ["HeyMelody", "HeyMelody.Host", "HeyMelody.DeviceManager", "HeyMelody.FitTest", "HeyMelody.HearingSetup"];
        foreach (var component in components)
        {
            var relative = component == "HeyMelody" ? "Contents/MacOS" : "Contents/Helpers/" + component + ".app/Contents/MacOS";
            TestDirectory.FileAt(Path.Combine(original, relative, Executable(component)));
        }
        var renamed = Path.Combine(scope.Root, "Renamed 产品 App.app"); Directory.Move(original, renamed);
        var helperBase = Path.Combine(renamed, "Contents/Helpers/HeyMelody.Host.app/Contents/MacOS");
        Program.Require(ApplicationPaths.FindApplicationBundle(helperBase) == renamed, "Nested helper resolved its own bundle as the application root.");
        Program.Require(ApplicationPaths.GetResourceRoot(helperBase) == Path.Combine(renamed, "Contents", "Resources"), "Helper did not find shared outer resources.");
        foreach (var component in components)
        {
            var expected = component == "HeyMelody" ? Path.Combine(renamed, "Contents/MacOS", Executable(component))
                : Path.Combine(renamed, "Contents/Helpers", component + ".app", "Contents/MacOS", Executable(component));
            var actual = ApplicationPaths.ResolveComponent(component == "HeyMelody" ? "HeyMelody.App" : component, helperBase);
            Program.Require(actual.FileName == Path.GetFullPath(expected) && actual.WorkingDirectory == Path.GetDirectoryName(Path.GetFullPath(expected)), "A renamed bundle component resolved incorrectly: " + component);
        }
        Program.ThrowsIOException(() => ApplicationPaths.ResolveComponent("HeyMelody.Missing", helperBase));
    }

    public static void SourceFallback()
    {
        using var scope = new TestDirectory(); var repo = scope.DirectoryAt("checkout with spaces");
        var caller = Path.Combine(repo, "src/HeyMelody.Client/bin/Release/net10.0"); Directory.CreateDirectory(caller);
        Directory.CreateDirectory(Path.Combine(repo, "src/HeyMelody.Resources"));
        var host = Path.Combine(repo, "src/HeyMelody.Host/bin/Release/net10.0/HeyMelody.Host.dll");
        var app = Path.Combine(repo, "src/HeyMelody.App/bin/Release/net10.0", RuntimeInformation.RuntimeIdentifier, "HeyMelody.dll");
        var manager = Path.Combine(repo, "src/HeyMelody.DeviceManager/bin/Debug/net10.0/HeyMelody.DeviceManager.dll");
        foreach (var path in new[] { host, app, manager }) TestDirectory.FileAt(path);
        foreach (var (component, expected) in new[] { ("HeyMelody.Host", host), ("HeyMelody.App", app), ("HeyMelody.DeviceManager", manager) })
        {
            var actual = ApplicationPaths.ResolveComponent(component, caller);
            Program.Require(actual.ArgumentList.Count == 1 && actual.ArgumentList[0] == Path.GetFullPath(expected), "Source DLL fallback is incorrect: " + component);
        }
        Program.Require(ApplicationPaths.GetResourceRoot(caller) == repo, "Source resource root did not resolve the checkout.");
    }

    private static string Executable(string name) => name + (OperatingSystem.IsWindows() ? ".exe" : "");
}
