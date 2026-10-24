using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HeyMelody.Diagnostics;
using HeyMelody.Rendering.Filament;

namespace HeyMelody.Rendering.Tests;

internal static class DiagnosticChecks
{
    internal static async Task RunAsync(string fixtureRoot, Action<bool, string> check)
    {
        var directory = Path.Combine(fixtureRoot, "diagnostic-output", Guid.NewGuid().ToString("N"));
        DiagnosticLog.ResetForTests();
        DiagnosticLog.InitializeIdentity("HeyMelody", "main");
        DiagnosticLog.Configure(new DiagnosticSessionInfo
        {
            Enabled = true, SessionId = Guid.NewGuid().ToString("N"), Components = ["HeyMelody"], Directory = directory
        });
        try
        {
            // The three-byte character crosses a 4096-byte read boundary. Both streams exceed the UI error summary.
            var stdout = Encoding.UTF8.GetBytes(new string('o', 4095) + "耳" + new string('x', 24_000) + "\r\nstdout-end");
            var stderr = Encoding.UTF8.GetBytes(new string('e', 25_000) + "错误\nstderr-end");
            var summaries = await Task.WhenAll(
                NativeBackendProbeProcess.CaptureOutputAsync(new MemoryStream(stdout), "stdout", "fixture", FilamentBackend.Vulkan, 42),
                NativeBackendProbeProcess.CaptureOutputAsync(new MemoryStream(stderr), "stderr", "fixture", FilamentBackend.Vulkan, 42));
            check(summaries.All(summary => summary.Length == NativeBackendProbeProcess.MaximumLogCharacters),
                "Probe UI error summaries retain their 8192-character cap.");
            var invalid = new byte[] { 0xff, 0xc3 };
            _ = await NativeBackendProbeProcess.CaptureOutputAsync(new MemoryStream(invalid), "invalid-utf8", "fixture", FilamentBackend.OpenGL);
            var apphost = Path.Combine(AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "HeyMelody.Rendering.Filament.Tests.exe" : "HeyMelody.Rendering.Filament.Tests");
            var child = await new NativeBackendProbeProcess(TimeSpan.FromSeconds(10)).RunAsync(apphost, FilamentBackend.OpenGL, default);
            if (!child.Succeeded) throw new InvalidOperationException("The isolated diagnostic fixture process failed.");
            var runner = new SuccessfulProbe();
            var probe = new NativeBackendProbe(runner);
            var payload = new NativeProbePayload(apphost, "diagnostic-cache-fixture");
            await probe.EnsureAvailableAsync(payload, FilamentBackend.OpenGL, default);
            await probe.EnsureAvailableAsync(payload, FilamentBackend.OpenGL, default);
            probe.Invalidate(payload, FilamentBackend.OpenGL);
            await probe.EnsureAvailableAsync(payload, FilamentBackend.OpenGL, default);

            var nativeText = "Native driver 错误\n";
            var native = Encoding.UTF8.GetBytes(nativeText);
            var address = Marshal.AllocHGlobal(native.Length);
            try
            {
                Marshal.Copy(native, 0, address, native.Length);
                NativeBindings.ReceiveNativeLog(3, address, (uint)native.Length, 0);
                Marshal.Copy(Enumerable.Repeat((byte)'x', native.Length).ToArray(), 0, address, native.Length);
            }
            finally { Marshal.FreeHGlobal(address); }
            DiagnosticLog.Flush();
            DiagnosticLog.Stop("Filament diagnostic checks completed");

            var records = Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories)
                .SelectMany(File.ReadAllLines).Where(line => line.Length != 0)
                .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToArray();
            foreach (var (name, expected) in new[] { ("stdout", stdout), ("stderr", stderr), ("invalid-utf8", invalid) })
            {
                var chunks = records.Where(record => record.GetProperty("source").GetString() == "Filament.Probe"
                    && record.GetProperty("eventName").GetString() == name
                    && record.GetProperty("data").GetProperty("executablePath").GetString() == "fixture")
                    .OrderBy(record => record.GetProperty("sequence").GetInt64()).ToArray();
                var bytes = Convert.FromHexString(string.Concat(chunks.Select(record => record.GetProperty("data").GetProperty("utf8Hex").GetString())));
                check(bytes.AsSpan().SequenceEqual(expected), $"Probe {name} preserves every raw byte, including output beyond the UI summary.");
                long offset = 0;
                foreach (var chunk in chunks)
                {
                    if (chunk.GetProperty("data").GetProperty("byteOffset").GetInt64() != offset)
                        throw new InvalidOperationException("Probe stream byte offsets are not contiguous.");
                    offset += chunk.GetProperty("data").GetProperty("byteLength").GetInt64();
                }
            }
            var stdoutText = string.Concat(records.Where(record => record.GetProperty("source").GetString() == "Filament.Probe"
                    && record.GetProperty("eventName").GetString() is "stdout" or "stdout.decodeEnd"
                    && record.GetProperty("data").GetProperty("executablePath").GetString() == "fixture")
                .OrderBy(record => record.GetProperty("sequence").GetInt64()).Select(record => record.GetProperty("message").GetString()));
            check(stdoutText == Encoding.UTF8.GetString(stdout), "Probe UTF8 decoding preserves a character split across stream reads.");
            foreach (var (name, expected) in new[] { ("stdout", "backend=opengl\n" + new string('o', 24_000)), ("stderr", new string('e', 24_000)) })
            {
                var chunks = records.Where(record => record.GetProperty("source").GetString() == "Filament.Probe"
                    && record.GetProperty("eventName").GetString() == name
                    && record.GetProperty("data").GetProperty("executablePath").GetString() == apphost)
                    .OrderBy(record => record.GetProperty("sequence").GetInt64());
                var bytes = Convert.FromHexString(string.Concat(chunks.Select(record => record.GetProperty("data").GetProperty("utf8Hex").GetString())));
                check(bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(expected)), $"The actual isolated process {name} is logged completely despite bounded result summaries.");
            }
            check(runner.Calls == 2 && records.Any(record => record.GetProperty("eventName").GetString() == "cacheHit")
                && records.Any(record => record.GetProperty("eventName").GetString() == "cacheInvalidated"),
                "Successful cache reuse and invalidation remain visible without starting an extra probe.");
            var callback = records.Single(record => record.GetProperty("source").GetString() == "Filament.Native");
            check(callback.GetProperty("message").GetString() == nativeText && callback.GetProperty("level").GetString() == "Error"
                && callback.GetProperty("data").GetProperty("utf8Hex").GetString() == Convert.ToHexString(native),
                "Native callback copies UTF8 bytes before the native buffer is overwritten/freed and maps its severity.");

            var fileCount = Directory.GetFiles(directory, "*.jsonl", SearchOption.AllDirectories).Length;
            _ = await NativeBackendProbeProcess.CaptureOutputAsync(new MemoryStream(stdout), "stdout", "fixture", FilamentBackend.Vulkan);
            check(Directory.GetFiles(directory, "*.jsonl", SearchOption.AllDirectories).Length == fileCount,
                "An ended diagnostic session is not reactivated by later native probe output.");
        }
        finally { DiagnosticLog.ResetForTests(); }
    }

    private sealed class SuccessfulProbe : INativeBackendProbeRunner
    {
        internal int Calls;
        public Task<NativeProbeResult> RunAsync(string executablePath, FilamentBackend backend, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new NativeProbeResult(0, "", ""));
        }
    }
}
