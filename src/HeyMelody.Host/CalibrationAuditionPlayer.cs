using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HeyMelodyWindows.Calibration;
using static HeyMelodyWindows.Host.WasapiNative;

using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Host;

internal interface ICalibrationAuditionPlayer : IAsyncDisposable
{
    event Action<IReadOnlyList<double>>? Spectrum;
    event Action? Failed;
    Task PrepareAsync(ulong address, CancellationToken token);
    Task PauseAsync(CancellationToken token);
    Task PlayAsync(CancellationToken token);
    Task StopAsync();
}

internal static partial class AuditionEndpointAddress
{
    [GeneratedRegex(@"(?:^|&)([0-9A-Fa-f]{12})(?:_C[0-9A-Fa-f]{8})?$", RegexOptions.CultureInvariant)]
    private static partial Regex AddressTail();
    internal static bool TryParse(string? id, out ulong address)
    {
        address = 0;
        if (id is null || !id.StartsWith(@"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_", StringComparison.OrdinalIgnoreCase)) return false;
        string[] segments = id.Split('\\');
        if (segments.Length != 3) return false;
        var match = AddressTail().Match(segments[2]);
        return match.Success && ulong.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address)
            && address is > 0 and <= 0xFFFFFFFFFFFF;
    }
}

internal sealed class AuditionWave
{
    internal short[] Samples { get; }
    internal int Frames => Samples.Length / 2;
    private AuditionWave(short[] samples) => Samples = samples;
    internal static AuditionWave Read(Stream stream)
    {
        if (stream.CanSeek) return Read(stream, Math.Max(0, stream.Length - stream.Position));
        // Chunk lengths are validated against the total size, so a stream that cannot report it is buffered first.
        using var memory = new MemoryStream(); stream.CopyTo(memory); memory.Position = 0;
        return Read(memory, memory.Length);
    }
    // Validation order and messages follow the chunk walk of a fully buffered file; only the PCM payload is allocated.
    private static AuditionWave Read(Stream source, long total)
    {
        try
        {
            Span<byte> head = stackalloc byte[12], chunk = stackalloc byte[16];
            if (total is < 44 or > 32_000_000) throw new InvalidDataException("试听音频不可用。");
            source.ReadExactly(head);
            if (!head[..4].SequenceEqual("RIFF"u8) || !head[8..].SequenceEqual("WAVE"u8) || BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) != total - 8)
                throw new InvalidDataException("试听音频不可用。");
            bool format = false; short[]? samples = null;
            long offset = 12;
            for (; offset + 8 <= total;)
            {
                source.ReadExactly(head[..8]);
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
                long available = total - offset - 8;
                if (length > available || ((length & 1) != 0 && length == available)) throw new InvalidDataException("试听音频不可用。");
                long skip = length + (length & 1);
                if (head[..4].SequenceEqual("fmt "u8))
                {
                    if (format || length < 16) throw new InvalidDataException("试听音频格式不可用。");
                    source.ReadExactly(chunk);
                    if (BinaryPrimitives.ReadUInt16LittleEndian(chunk) != 1
                        || BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]) != 2 || BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != 48000
                        || BinaryPrimitives.ReadUInt32LittleEndian(chunk[8..]) != 192000 || BinaryPrimitives.ReadUInt16LittleEndian(chunk[12..]) != 4
                        || BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]) != 16) throw new InvalidDataException("试听音频格式不可用。");
                    format = true; skip -= 16;
                }
                else if (head[..4].SequenceEqual("data"u8))
                {
                    if (samples is not null || length == 0 || length % 4 != 0) throw new InvalidDataException("试听音频不可用。");
                    samples = new short[length / 2];
                    source.ReadExactly(MemoryMarshal.AsBytes(samples.AsSpan()));
                    if (!BitConverter.IsLittleEndian)
                        for (int i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReverseEndianness(samples[i]);
                    skip = 0;
                }
                if (skip != 0) source.Seek(skip, SeekOrigin.Current);
                offset += 8 + length + (length & 1);
            }
            if (offset != total || !format || samples is null) throw new InvalidDataException("试听音频不可用。");
            return new(samples);
        }
        catch (EndOfStreamException) { throw new InvalidDataException("试听音频不可用。"); }
    }
}

/// <summary>One private shared-mode stream. First activation and all COM calls stay on a dedicated STA thread.</summary>
internal sealed class WasapiCalibrationAuditionPlayer : ICalibrationAuditionPlayer
{
    private sealed record Command(Action Action, CancellationToken Token, TaskCompletionSource Completion, string? CorrelationId);
    private readonly BlockingCollection<Command> _commands = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly Func<int> _initializeCom;
    private readonly Action _uninitializeCom;
    private readonly Func<bool> _exemptThread;
    private int _disposed;
    private IDeviceEnumerator? _enumerator;
    private IDevice? _endpoint;
    private object? _clientObject, _renderObject;
    private IAudioClient? _client;
    private IRenderClient? _render;
    private AuditionWave? _wave;
    private uint _capacity;
    private short[]? _fill;
    private int _cursor;
    private bool _playing;
    private long _lastSpectrum;
    private readonly HearingSpectrumAnalyzer _analyzer = new();
    private readonly float[] _mono = new float[HearingSpectrumAnalyzer.FrameLength];
    public event Action<IReadOnlyList<double>>? Spectrum;
    public event Action? Failed;
    internal WasapiCalibrationAuditionPlayer(Func<int>? initializeCom = null, Action? uninitializeCom = null, Func<bool>? exemptThread = null)
    {
        _initializeCom = initializeCom ?? (() => CoInitializeEx(IntPtr.Zero, 2));
        _uninitializeCom = uninitializeCom ?? CoUninitialize;
        _exemptThread = exemptThread ?? WindowsProcessEnergy.TryExemptCurrentThread;
        _thread = new(Run) { IsBackground = true, Name = "HeyMelody calibration audition" };
        _thread.SetApartmentState(ApartmentState.STA); _thread.Start();
    }
    public Task PrepareAsync(ulong address, CancellationToken token) => Enqueue(() => Prepare(address, token), token);
    public Task PauseAsync(CancellationToken token) => Enqueue(Pause, token);
    public Task PlayAsync(CancellationToken token) => Enqueue(() =>
    {
        if (_client is null || _render is null || _wave is null) throw new InvalidOperationException("耳机音频输出尚未就绪。");
        if (_playing) return;
        Fill(); Check(_client.Start()); _playing = true; _lastSpectrum = 0;
        DiagnosticLog.Info("host.audio", "playing", "WASAPI audition playback started");
    }, token);
    public Task StopAsync() => Volatile.Read(ref _disposed) != 0 ? _finished.Task : Enqueue(Cleanup, CancellationToken.None);
    private Task Enqueue(Action action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0) return Task.FromException(new ObjectDisposedException(nameof(WasapiCalibrationAuditionPlayer)));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { _commands.Add(new(action, token, completion, DiagnosticLog.CurrentCorrelationId)); }
        catch (InvalidOperationException) { completion.TrySetException(new ObjectDisposedException(nameof(WasapiCalibrationAuditionPlayer))); }
        return completion.Task;
    }
    private void Run()
    {
        using var background = DiagnosticLog.SuspendOperation();
        bool initialized = false;
        try
        {
            Check(_initializeCom()); initialized = true;
            DiagnosticLog.Debug("host.audio", "threadStarted", "WASAPI audition COM thread initialized");
            // The tray may be in EcoQoS while a preview plays; the 5 ms pump must not be throttled. A failed exemption only costs efficiency.
            try { _exemptThread(); } catch { }
            while (!_commands.IsCompleted)
            {
                // Idle: block until a command arrives or CompleteAdding wakes the thread; no polling while nothing plays.
                if (_commands.TryTake(out var command, _playing ? 5 : Timeout.Infinite))
                {
                    using var operation = command.CorrelationId is { } id ? DiagnosticLog.BeginOperation(id) : DiagnosticLog.SuspendOperation();
                    try { command.Token.ThrowIfCancellationRequested(); command.Action(); command.Completion.TrySetResult(); }
                    catch (OperationCanceledException) { command.Completion.TrySetCanceled(command.Token); }
                    catch (Exception error)
                    {
                        DiagnosticLog.Debug("host.audio", "commandFailed", "WASAPI audition command failed; caller receives the original exception",
                            new { action = command.Action.Method.Name, exceptionType = error.GetType().FullName, error.HResult, error.Message });
                        command.Completion.TrySetException(error);
                    }
                }
                if (!_playing) continue;
                try
                {
                    Fill();
                    long now = Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(_lastSpectrum, now) >= TimeSpan.FromMilliseconds(50))
                    {
                        _lastSpectrum = now; PublishSpectrum();
                    }
                }
                catch (Exception error)
                {
                    DiagnosticLog.Error("host.audio", "renderFailed", "WASAPI audition render pump failed", error);
                    Cleanup(); try { Failed?.Invoke(); } catch { }
                }
            }
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("host.audio", "threadFailed", "WASAPI audition COM thread failed", error);
            Interlocked.Exchange(ref _disposed, 1); _commands.CompleteAdding();
            while (_commands.TryTake(out var command)) command.Completion.TrySetException(error);
        }
        finally
        {
            try { Cleanup(); if (initialized) _uninitializeCom(); }
            catch { }
            finally { _finished.TrySetResult(); }
        }
    }
    private void Prepare(ulong address, CancellationToken token)
    {
        if (_client is not null) return;
        if (address is 0 or > 0xFFFFFFFFFFFF) throw new ArgumentOutOfRangeException(nameof(address));
        try
        {
            DiagnosticLog.Debug("host.audio", "preparing", "Resolving the headset WASAPI audition endpoint", new { address });
            using var stream = typeof(WasapiCalibrationAuditionPlayer).Assembly.GetManifestResourceStream("HeyMelody.Host.Assets.hearing_enhancement_audition.wav")
                ?? throw new InvalidDataException("试听音频不可用。");
            _wave = AuditionWave.Read(stream);
            _enumerator = (IDeviceEnumerator)new DeviceEnumeratorClass();
            var trace = new EndpointTrace();
            _endpoint = EndpointIdentity.WaitForStable(() => EndpointIdentity.Resolve(_enumerator, address, trace), token, trace);
            token.ThrowIfCancellationRequested();
            Check(_endpoint.Activate(AudioClientId, 1, IntPtr.Zero, out _clientObject)); _client = (IAudioClient)_clientObject;
            IntPtr format = Marshal.AllocCoTaskMem(18);
            try
            {
                byte[] data = new byte[18];
                BinaryPrimitives.WriteUInt16LittleEndian(data, 1); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 2);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 48000); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 192000);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 4); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), 16);
                Marshal.Copy(data, 0, format, 18);
                // AUTOCONVERTPCM | SRC_DEFAULT_QUALITY lets the shared engine use its existing mix without changing driver/default settings.
                Check(_client.Initialize(0, 0x88000000, 500000, 0, format, IntPtr.Zero));
            }
            finally { Marshal.FreeCoTaskMem(format); }
            Check(_client.GetBufferSize(out _capacity));
            if (_capacity is 0 or > 96000) throw new InvalidDataException("耳机音频缓冲区不可用。");
            Check(_client.GetService(RenderClientId, out _renderObject)); _render = (IRenderClient)_renderObject;
            _cursor = 0; _analyzer.Reset();
            DiagnosticLog.Info("host.audio", "prepared", "Headset WASAPI audition endpoint is ready",
                new { address, bufferFrames = _capacity, sampleRate = 48000, channels = 2, bitsPerSample = 16 });
        }
        catch { Cleanup(); throw; }
    }
    private void Fill()
    {
        Check(_client!.GetCurrentPadding(out uint padding));
        if (padding > _capacity) throw new InvalidDataException();
        uint count = _capacity - padding;
        if (count == 0) return;
        Check(_render!.GetBuffer(count, out IntPtr buffer));
        try
        {
            // Sized for the whole endpoint buffer once: the pump runs every 5 ms while playing, so a fresh array per
            // pass was steady gen0 churn for the lifetime of the preview.
            int length = checked((int)count * 2);
            if (_fill is null || _fill.Length < length) _fill = new short[checked((int)_capacity * 2)];
            var output = _fill;
            for (int i = 0; i < count; i++)
            {
                output[i * 2] = _wave!.Samples[_cursor * 2]; output[i * 2 + 1] = _wave.Samples[_cursor * 2 + 1];
                _cursor = (_cursor + 1) % _wave.Frames;
            }
            Marshal.Copy(output, 0, buffer, length);
        }
        finally { Check(_render.ReleaseBuffer(count, 0)); }
    }
    private void Pause()
    {
        if (!_playing || _client is null) return;
        Check(_client.Stop()); _playing = false;
        Check(_client.GetCurrentPadding(out uint padding));
        _cursor = (_cursor - (int)padding % _wave!.Frames + _wave.Frames) % _wave.Frames;
        Check(_client.Reset()); _analyzer.Reset();
    }
    private void PublishSpectrum()
    {
        Check(_client!.GetCurrentPadding(out uint padding));
        int start = (_cursor - (int)padding % _wave!.Frames + _wave.Frames) % _wave.Frames;
        // Analyze takes a span and returns a fresh result, so it cannot keep the frame and one buffer serves every call.
        float[] mono = _mono;
        for (int i = 0; i < mono.Length; i++)
        {
            int sample = ((start + i) % _wave.Frames) * 2;
            mono[i] = (_wave.Samples[sample] + _wave.Samples[sample + 1]) / 65536f;
        }
        try { Spectrum?.Invoke(_analyzer.Analyze(mono)); } catch { }
    }
    private void Cleanup()
    {
        if (_playing) { try { _client?.Stop(); } catch { } }
        _playing = false;
        foreach (object? value in new object?[] { _renderObject, _clientObject, _endpoint, _enumerator })
            try { Release(value); } catch { }
        _renderObject = _clientObject = null; _render = null; _client = null; _endpoint = null; _enumerator = null;
        _wave = null; _fill = null; _cursor = 0;
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _commands.CompleteAdding();
        await _finished.Task;
    }
}
