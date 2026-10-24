using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HeyMelody.Diagnostics;

namespace HeyMelody.Rendering.Filament;

internal static class FilamentBackendPolicy
{
    internal static void Validate(FilamentBackend backend)
    {
        if (backend is not (FilamentBackend.OpenGL or FilamentBackend.Metal or FilamentBackend.Vulkan))
            throw new ArgumentOutOfRangeException(nameof(backend), "A specific compiled Filament backend is required.");
    }

    internal static string Argument(FilamentBackend backend) => backend switch
    {
        FilamentBackend.OpenGL => "opengl", FilamentBackend.Metal => "metal", FilamentBackend.Vulkan => "vulkan",
        _ => throw new ArgumentOutOfRangeException(nameof(backend))
    };

    internal static void ValidateDiagnostics(FilamentBackend expected, uint requested, uint actual, FilamentFailureStage stage)
    {
        if (requested != (uint)expected || actual != (uint)expected)
            throw new FilamentRendererException(
                $"The native renderer returned backend {actual} for requested backend {expected} (native request {requested}).",
                FilamentFailureKind.RuntimeFailure, stage, expected);
    }
}

internal readonly record struct NativeProbePayload(string ExecutablePath, string Identity)
{
    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

internal readonly record struct NativeProbeResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut = false)
{
    internal bool Succeeded => !TimedOut && ExitCode == 0;
}

internal readonly record struct NativeProbeObservation(FilamentProbeDisposition Disposition, double WaitMilliseconds);

internal interface INativeBackendProbeRunner
{
    Task<NativeProbeResult> RunAsync(string executablePath, FilamentBackend backend, CancellationToken cancellationToken);
}

/// <summary>Callers share one child per payload/backend; cancellation only abandons the caller's wait.</summary>
internal sealed class NativeBackendProbe(INativeBackendProbeRunner runner)
{
    internal static NativeBackendProbe Shared { get; } = new(new NativeBackendProbeProcess());
    private readonly object _gate = new();
    private readonly HashSet<(string Payload, FilamentBackend Backend)> _successes = [];
    private readonly Dictionary<(string Payload, FilamentBackend Backend), long> _generations = [];
    private readonly Dictionary<(string Payload, FilamentBackend Backend), ProbeOperation> _inflight = [];

    internal NativeProbeObservation EnsureAvailable(NativeProbePayload payload, FilamentBackend backend, CancellationToken cancellationToken)
        => EnsureAvailableAsync(payload, backend, cancellationToken).GetAwaiter().GetResult();

    internal async Task<NativeProbeObservation> EnsureAvailableAsync(
        NativeProbePayload payload, FilamentBackend backend, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        FilamentBackendPolicy.Validate(backend);
        cancellationToken.ThrowIfCancellationRequested();
        var key = (payload.Identity, backend);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeOperation operation;
            bool ownsStart = false;
            lock (_gate)
            {
                if (_successes.Contains(key))
                {
                    DiagnosticLog.Debug("Filament.Probe", "cacheHit", "A successful probe for the exact native payload/backend is cached.",
                        new { backend, payload.ExecutablePath, payload.Identity });
                    return new(FilamentProbeDisposition.Cached, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                if (!_inflight.TryGetValue(key, out operation!))
                {
                    operation = new(_generations.GetValueOrDefault(key));
                    _inflight.Add(key, operation);
                    ownsStart = true;
                }
            }
            DiagnosticLog.Debug("Filament.Probe", ownsStart ? "started" : "joined", "Waiting for the shared backend probe.",
                new { backend, payload.ExecutablePath, payload.Identity, operation.Generation });
            if (ownsStart) _ = RunProbeAsync(payload, backend, key, operation);
            NativeProbeResult result;
            try
            {
                result = await operation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                lock (_gate)
                    if (_generations.GetValueOrDefault(key) != operation.Generation) continue;
                throw;
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
                if (_generations.GetValueOrDefault(key) != operation.Generation) continue;
            ThrowIfUnavailable(result, backend);
            return new(ownsStart ? FilamentProbeDisposition.Started : FilamentProbeDisposition.Joined,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private async Task RunProbeAsync(NativeProbePayload payload, FilamentBackend backend,
        (string Payload, FilamentBackend Backend) key, ProbeOperation operation)
    {
        NativeProbeResult result = default;
        Exception? failure = null;
        var started = Stopwatch.GetTimestamp();
        try
        {
            result = await runner.RunAsync(payload.ExecutablePath, backend, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("Filament.Probe", "runnerFailed", "The backend probe runner failed.", error,
                new { backend, payload.ExecutablePath, payload.Identity });
            failure = error is Win32Exception or IOException or UnauthorizedAccessException
                ? new FilamentRendererException("The Filament backend probe component could not be started.",
                    FilamentFailureKind.ComponentUnavailable, FilamentFailureStage.Probe, backend, innerException: error)
                : error;
        }
        DiagnosticLog.Write(failure is null && result.Succeeded ? DiagnosticLevel.Information : DiagnosticLevel.Warning,
            "Filament.Probe", "completed", "Backend probe completed.",
            new { backend, payload.ExecutablePath, payload.Identity, result.ExitCode, result.TimedOut,
                durationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                summaryOutputCharacters = result.StandardOutput?.Length ?? 0, summaryErrorCharacters = result.StandardError?.Length ?? 0 });
        lock (_gate)
        {
            // Retain an invalidated operation until its child exits: a new generation cannot overlap it.
            if (failure is null && result.Succeeded && _generations.GetValueOrDefault(key) == operation.Generation)
                _successes.Add(key);
            if (_inflight.TryGetValue(key, out var current) && ReferenceEquals(current, operation))
                _inflight.Remove(key);
            if (failure is null) operation.Completion.TrySetResult(result);
            else operation.Completion.TrySetException(failure);
        }
    }

    private static void ThrowIfUnavailable(NativeProbeResult result, FilamentBackend backend)
    {
        if (!result.Succeeded)
        {
            var reason = result.TimedOut ? "timed out after 30 seconds" : $"exited with code {result.ExitCode}";
            var detail = string.Join("\n", new[] { result.StandardError.Trim(), result.StandardOutput.Trim() }
                .Where(value => value.Length != 0));
            DiagnosticLog.Error("Filament.Probe", "unavailable", detail,
                data: new { backend, result.ExitCode, result.TimedOut });
            throw new FilamentRendererException($"The {backend} backend probe {reason}. {detail}".Trim(),
                FilamentFailureKind.BackendUnavailable, FilamentFailureStage.Probe, backend);
        }
    }

    internal void Invalidate(NativeProbePayload payload, FilamentBackend backend)
    {
        var key = (payload.Identity, backend);
        lock (_gate)
        {
            _successes.Remove(key);
            _generations[key] = _generations.GetValueOrDefault(key) + 1;
        }
        DiagnosticLog.Debug("Filament.Probe", "cacheInvalidated", "The native payload/backend must be probed again before reuse.",
            new { backend, payload.ExecutablePath, payload.Identity });
    }

    private sealed class ProbeOperation(long generation)
    {
        internal long Generation { get; } = generation;
        internal TaskCompletionSource<NativeProbeResult> Completion { get; } = CreateCompletion();

        private static TaskCompletionSource<NativeProbeResult> CreateCompletion()
        {
            var completion = new TaskCompletionSource<NativeProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            // All callers may cancel before the shared child faults. Its exception still needs observing.
            _ = completion.Task.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return completion;
        }
    }
}

internal sealed class NativeBackendProbeProcess(TimeSpan? timeout = null) : INativeBackendProbeRunner
{
    internal const int MaximumLogCharacters = 8192;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);

    public async Task<NativeProbeResult> RunAsync(string executablePath, FilamentBackend backend, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(executablePath)) throw new IOException("The backend probe path must be absolute.");
        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!
        };
        start.ArgumentList.Add("--backend");
        start.ArgumentList.Add(FilamentBackendPolicy.Argument(backend));
        DiagnosticLog.Debug("Filament.Probe", "processStarting", "Starting the hidden native backend probe.",
            new { executablePath, backend, timeoutMilliseconds = _timeout.TotalMilliseconds });
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("The backend probe process did not start.");
        DiagnosticLog.Debug("Filament.Probe", "processStarted", "Native backend probe process started.", new { executablePath, backend, process.Id });
        var output = CaptureOutputAsync(process.StandardOutput.BaseStream, "stdout", executablePath, backend, process.Id);
        var error = CaptureOutputAsync(process.StandardError.BaseStream, "stderr", executablePath, backend, process.Id);
        using var deadline = new CancellationTokenSource(_timeout);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Warning("Filament.Probe", "processStopping", "Stopping the owned backend probe after cancellation or timeout.",
                data: new { executablePath, backend, process.Id, callerCancelled = cancellationToken.IsCancellationRequested });
            KillOwnedProcess(process);
            await DrainAfterKillAsync(process).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                await ObserveLogsAsync(output, error).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            timedOut = true;
        }
        var logs = await ObserveLogsAsync(output, error).ConfigureAwait(false);
        return new(process.HasExited ? process.ExitCode : -1, logs.Output, logs.Error, timedOut);
    }

    internal static async Task<string> CaptureOutputAsync(Stream reader, string streamName, string executablePath,
        FilamentBackend backend, int? processId = null)
    {
        var text = new StringBuilder();
        var buffer = new byte[4096];
        var characters = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        long offset = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            var decoded = decoder.GetChars(buffer, 0, read, characters, 0, flush: false);
            if (DiagnosticLog.IsEnabled)
                DiagnosticLog.Debug("Filament.Probe", streamName, new string(characters, 0, decoded),
                    new { executablePath, backend, processId, byteOffset = offset, byteLength = read,
                        utf8Hex = Convert.ToHexString(buffer.AsSpan(0, read)) });
            offset += read;
            var count = Math.Min(decoded, MaximumLogCharacters - text.Length);
            if (count > 0) text.Append(characters, 0, count);
        }
        var remaining = decoder.GetChars([], 0, 0, characters, 0, flush: true);
        if (remaining > 0)
        {
            var decoded = new string(characters, 0, remaining);
            DiagnosticLog.Debug("Filament.Probe", streamName + ".decodeEnd", decoded,
                new { executablePath, backend, processId, byteOffset = offset });
            text.Append(characters, 0, Math.Min(remaining, MaximumLogCharacters - text.Length));
        }
        DiagnosticLog.Debug("Filament.Probe", streamName + ".end", "Probe stream drained.",
            new { executablePath, backend, processId, bytes = offset, summaryCharacters = text.Length });
        return text.ToString();
    }

    private static void KillOwnedProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static async Task DrainAfterKillAsync(Process process)
    {
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    private static async Task<(string Output, string Error)> ObserveLogsAsync(Task<string> output, Task<string> error)
    {
        try
        {
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return (output.Result, error.Result);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException)
        {
            DiagnosticLog.Warning("Filament.Probe", "streamDrainIncomplete", "A native probe stream could not be completely drained.", exception);
            // Only owned probe streams are involved. Observe any late failures after their handles close.
            _ = Task.WhenAll(output, error).ContinueWith(task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return (output.IsCompletedSuccessfully ? output.Result : "", error.IsCompletedSuccessfully ? error.Result : "");
        }
    }
}
