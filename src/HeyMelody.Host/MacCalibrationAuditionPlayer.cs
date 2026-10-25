#if HEYMELODY_MACOS
using HeyMelody.MacOS;
#endif
using HeyMelody.Platform;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Calibration;

namespace HeyMelodyWindows.Host;

internal interface ISelectableCalibrationAuditionPlayer
{
    string? SelectedOutputToken { get; }
    IReadOnlyList<UiPreviewAudioOutput> GetOutputs();
    Task SelectOutputAsync(string token, CancellationToken cancellationToken);
    void ResetSelection();
}

/// <summary>The macOS audition stream is pinned to a user-selected Bluetooth output, never the system default.</summary>
#if HEYMELODY_MACOS
internal sealed class MacCalibrationAuditionPlayer : ICalibrationAuditionPlayer, ISelectableCalibrationAuditionPlayer
{
    private readonly object _sync = new();
    private readonly Func<IReadOnlyList<AudioOutputDevice>> _enumerate;
    private readonly Func<IAudioPlayback> _createPlayback;
    private readonly Dictionary<string, AudioOutputDevice> _outputs = [];
    private IAudioPlayback? _playback;
    private AuditionWave? _wave;
    private CancellationTokenSource? _monitorLifetime;
    private Task _monitor = Task.CompletedTask;
    private bool _playing, _disposed;
    private readonly HearingSpectrumAnalyzer _analyzer = new();
    internal MacCalibrationAuditionPlayer(Func<IReadOnlyList<AudioOutputDevice>>? enumerate = null,
        Func<IAudioPlayback>? createPlayback = null)
    { _enumerate = enumerate ?? MacAudioOutput.EnumerateOutputs; _createPlayback = createPlayback ?? (() => new MacAudioQueue()); }
    public event Action<IReadOnlyList<double>>? Spectrum;
    public event Action? Failed;
    public string? SelectedOutputToken { get; private set; }
    public IReadOnlyList<UiPreviewAudioOutput> GetOutputs()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var devices = _enumerate().DistinctBy(x => x.Id).ToArray();
            foreach (var token in _outputs.Keys.ToArray())
                if (!devices.Any(x => x.Id == _outputs[token].Id)) _outputs.Remove(token);
            foreach (var device in devices)
            {
                var token = _outputs.FirstOrDefault(x => x.Value.Id == device.Id).Key;
                if (token is null) token = Guid.NewGuid().ToString("N");
                _outputs[token] = device;
            }
            if (SelectedOutputToken is { } selected && !_outputs.ContainsKey(selected))
            {
                bool hadPlayback = _playback is not null;
                DropPlaybackLocked(); SelectedOutputToken = null;
                _monitorLifetime?.Cancel(); _monitorLifetime?.Dispose(); _monitorLifetime = null;
                if (hadPlayback) ThreadPool.QueueUserWorkItem(_ => NotifyFailed());
            }
            return _outputs.Select((entry, index) => new UiPreviewAudioOutput(entry.Key,
                devices.Count(x => x.Name == entry.Value.Name) > 1 ? $"{entry.Value.Name} · {index + 1}" : entry.Value.Name)).ToArray();
        }
    }
    public async Task SelectOutputAsync(string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await StopAsync();
        lock (_sync)
        {
            _ = GetOutputs();
            if (!_outputs.ContainsKey(token)) throw new InvalidOperationException("所选蓝牙音频输出已变化，请刷新后重新选择。");
            SelectedOutputToken = token;
        }
    }
    public void ResetSelection()
    {
        lock (_sync)
        {
            _monitorLifetime?.Cancel(); _monitorLifetime?.Dispose(); _monitorLifetime = null;
            DropPlaybackLocked(); SelectedOutputToken = null; _outputs.Clear();
        }
    }
    public Task PrepareAsync(ulong address, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _ = GetOutputs();
            if (SelectedOutputToken is not { } selected || !_outputs.TryGetValue(selected, out var output))
                throw new InvalidOperationException("请先选择与当前耳机对应的蓝牙音频输出。");
            if (_playback is not null) return Task.CompletedTask;
            using var stream = typeof(MacCalibrationAuditionPlayer).Assembly.GetManifestResourceStream("HeyMelody.Host.Assets.hearing_enhancement_audition.wav")
                ?? throw new InvalidDataException("试听音频不可用。");
            _wave = AuditionWave.Read(stream);
            var playback = _createPlayback();
            try { playback.Prepare(output.Id, _wave.Samples, 48000); }
            catch { playback.Dispose(); throw; }
            _playback = playback;
            _monitorLifetime = new();
            _monitor = MonitorAsync(_monitorLifetime.Token);
        }
        return Task.CompletedTask;
    }
    public Task PlayAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_playback is null) throw new InvalidOperationException("耳机音频输出尚未就绪。");
            _playback.Play(); _playing = true;
        }
        return Task.CompletedTask;
    }
    public Task PauseAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync) { _playing = false; _playback?.Pause(); }
        return Task.CompletedTask;
    }
    private async Task MonitorAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                IReadOnlyList<double>? levels = null; bool failed;
                lock (_sync)
                {
                    failed = _playback?.HasFailed == true;
                    if (failed)
                    {
                        DropPlaybackLocked(); SelectedOutputToken = null; _outputs.Clear();
                        _monitorLifetime?.Dispose(); _monitorLifetime = null;
                    }
                    else if (_playing && _wave is { } wave && _playback is { } playback)
                    {
                        var mono = new float[HearingSpectrumAnalyzer.FrameLength];
                        int cursor = (int)(playback.PlaybackFrame % wave.Frames);
                        for (int i = 0; i < mono.Length; i++)
                        { int frame = (cursor + i) % wave.Frames; mono[i] = (wave.Samples[frame * 2] + wave.Samples[frame * 2 + 1]) / 65536f; }
                        levels = _analyzer.Analyze(mono);
                    }
                }
                if (failed) { NotifyFailed(); return; }
                if (levels is not null) Spectrum?.Invoke(levels);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            lock (_sync)
            {
                DropPlaybackLocked(); SelectedOutputToken = null; _outputs.Clear();
                _monitorLifetime?.Dispose(); _monitorLifetime = null;
            }
            NotifyFailed();
        }
    }
    private void DropPlaybackLocked()
    {
        _playing = false; var playback = _playback; _playback = null;
        try { playback?.Dispose(); } catch { }
    }
    private void NotifyFailed() { try { Failed?.Invoke(); } catch { } }
    public async Task StopAsync()
    {
        Task monitor; CancellationTokenSource? lifetime;
        lock (_sync)
        {
            _playing = false;
            lifetime = _monitorLifetime; _monitorLifetime = null; lifetime?.Cancel(); monitor = _monitor;
            _playback?.Dispose(); _playback = null;
        }
        await monitor; lifetime?.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        lock (_sync) { _disposed = true; ResetSelection(); }
    }
}
#endif
