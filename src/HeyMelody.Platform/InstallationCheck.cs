using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HeyMelody.Platform;

/// <summary>A read-only packaged-entry smoke check; it never starts a UI, backend, radio or audio device.</summary>
public static class InstallationCheck
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args is not ["--verify-installation"]) return false;
        try
        {
            if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
                throw new PlatformNotSupportedException("此安装检查仅用于 Apple Silicon macOS 包。");
            var bundle = ApplicationPaths.FindApplicationBundle(AppContext.BaseDirectory)
                ?? throw new InvalidDataException("未从完整的应用 bundle 启动。");
            string[] components = ["HeyMelody", "HeyMelody.Host", "HeyMelody.DeviceManager", "HeyMelody.FitTest", "HeyMelody.HearingSetup"];
            foreach (var component in components)
            {
                var start = ApplicationPaths.ResolveComponent(component);
                if (!start.FileName.StartsWith(bundle + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException("应用组件超出当前 bundle。");
            }
            var resources = ApplicationPaths.ResourceRoot;
            foreach (var name in new[] { "artwork.json", "provenance.json" })
                if (!File.Exists(Path.Combine(resources, "ResourceBundles", name)))
                    throw new FileNotFoundException("预装资源缺失。", name);
            var mainNative = Path.Combine(bundle, "Contents", "MacOS");
            var hostNative = Path.GetDirectoryName(ApplicationPaths.ResolveComponent("HeyMelody.Host").FileName)!;
            foreach (var path in new[] { Path.Combine(mainNative, "libHeyMelody.Filament.Native.dylib"), Path.Combine(hostNative, "libHeyMelodyMac.dylib") })
            {
                var handle = NativeLibrary.Load(path);
                NativeLibrary.Free(handle);
            }
            var assembly = Assembly.GetEntryAssembly()!;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                ?? assembly.GetName().Version?.ToString(3);
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, component = assembly.GetName().Name, version,
                runtimeIdentifier = "osx-arm64", resourceBundles = true, nativeLibrariesLoaded = true,
                nativeBluetoothCalls = 0, audioPlaybackCalls = 0 }));
        }
        catch (Exception error)
        {
            exitCode = 1;
            Console.WriteLine(JsonSerializer.Serialize(new { success = false, error = error.GetType().Name, message = error.Message,
                nativeBluetoothCalls = 0, audioPlaybackCalls = 0 }));
        }
        return true;
    }
}
