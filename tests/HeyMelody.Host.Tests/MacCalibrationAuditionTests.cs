using System.Text.Json;
using HeyMelody.Platform;
using HeyMelodyWindows.Host;

internal static class MacCalibrationAuditionTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
#if HEYMELODY_MACOS
        await test("mac-audition-requires-explicit-output-without-native-fallback", async () =>
        {
            int created = 0;
            await using var player = new MacCalibrationAuditionPlayer(() => Devices(), () => { created++; return new Playback(); });
            await Throws<InvalidOperationException>(() => player.PrepareAsync(1, default));
            await Throws<InvalidOperationException>(() => player.PlayAsync(default));
            await Throws<InvalidOperationException>(() => player.SelectOutputAsync("uid-private-a", default));
            Check(created == 0, "No queue can be created before a valid explicit selection.");
        });
        await test("mac-audition-output-tokens-opaque-stable-and-reset-scoped", async () =>
        {
            IReadOnlyList<AudioOutputDevice> devices = Devices();
            await using var player = new MacCalibrationAuditionPlayer(() => devices, () => new Playback());
            var outputs = player.GetOutputs();
            Check(outputs.Count == 2 && outputs.Select(x => x.Token).Distinct().Count() == 2, "Separate output identities.");
            Check(outputs.All(x => Guid.TryParseExact(x.Token, "N", out _)), "Tokens are opaque GUIDs.");
            Check(!JsonSerializer.Serialize(outputs).Contains("uid-private", StringComparison.Ordinal), "Native identities stay out of UI records.");
            Check(outputs[0].Name != outputs[1].Name, "Duplicate display names remain distinguishable.");
            devices = devices.Reverse().ToArray();
            Check(player.GetOutputs().Select(x => x.Token).ToHashSet().SetEquals(outputs.Select(x => x.Token)), "Reordering does not replace identities.");
            await player.SelectOutputAsync(outputs[0].Token, default);
            player.ResetSelection();
            Check(player.SelectedOutputToken is null, "Reset clears selection.");
            await Throws<InvalidOperationException>(() => player.SelectOutputAsync(outputs[0].Token, default));
            await using var other = new MacCalibrationAuditionPlayer(() => devices, () => new Playback());
            await Throws<InvalidOperationException>(() => other.SelectOutputAsync(outputs[1].Token, default));
        });
        await test("mac-audition-routes-chosen-id-even-after-output-reorder", async () =>
        {
            IReadOnlyList<AudioOutputDevice> devices = Devices();
            var playback = new Playback();
            await using var player = new MacCalibrationAuditionPlayer(() => devices, () => playback);
            var selected = player.GetOutputs()[1].Token;
            await player.SelectOutputAsync(selected, default);
            devices = devices.Reverse().ToArray();
            await player.PrepareAsync(0x112233445566, default);
            Check(playback.PreparedId == "uid-private-b", "The selected output ID, never the first/default or peer address, is routed.");
            Check(playback.SampleRate == 48000 && playback.SampleCount > 0 && playback.SampleCount % 2 == 0, "Embedded audition PCM is stereo at the declared rate.");
        });
        await test("mac-audition-lost-choice-never-selects-remaining-output", async () =>
        {
            IReadOnlyList<AudioOutputDevice> devices = Devices(); int created = 0;
            await using var player = new MacCalibrationAuditionPlayer(() => devices, () => { created++; return new Playback(); });
            var token = player.GetOutputs()[1].Token;
            await player.SelectOutputAsync(token, default);
            devices = [devices[0]];
            await Throws<InvalidOperationException>(() => player.PrepareAsync(1, default));
            Check(player.SelectedOutputToken is null && created == 0, "Missing explicit destination does not fall back.");
        });
        await test("mac-audition-pause-resume-stop-retains-only-explicit-choice", async () =>
        {
            var first = new Playback(); var second = new Playback(); var queue = new Queue<Playback>([first, second]);
            await using var player = new MacCalibrationAuditionPlayer(() => Devices(), () => queue.Dequeue());
            var token = player.GetOutputs()[0].Token;
            await player.SelectOutputAsync(token, default); await player.PrepareAsync(1, default);
            await player.PlayAsync(default); await player.PauseAsync(default); await player.PlayAsync(default);
            Check(first.PlayCount == 2 && first.PauseCount == 1, "Pause and resume use the same explicit queue.");
            await player.StopAsync();
            Check(first.DisposeCount == 1 && player.SelectedOutputToken == token, "Stop releases playback while preserving the explicit choice.");
            await Throws<InvalidOperationException>(() => player.PlayAsync(default));
            await player.PrepareAsync(1, default); await player.PlayAsync(default);
            Check(second.PreparedId == "uid-private-a" && second.PlayCount == 1, "A fresh queue uses the same chosen ID.");
        });
        await test("mac-audition-canceled-operations-never-prepare-or-play", async () =>
        {
            int created = 0; var playback = new Playback();
            await using var player = new MacCalibrationAuditionPlayer(() => Devices(), () => { created++; return playback; });
            var token = player.GetOutputs()[0].Token;
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Throws<OperationCanceledException>(() => player.SelectOutputAsync(token, cancellation.Token));
            await player.SelectOutputAsync(token, default);
            await Throws<OperationCanceledException>(() => player.PrepareAsync(1, cancellation.Token));
            Check(created == 0, "Canceled preparation creates no queue.");
            await player.PrepareAsync(1, default);
            await Throws<OperationCanceledException>(() => player.PlayAsync(cancellation.Token));
            Check(playback.PlayCount == 0, "Canceled play submits no playback.");
        });
        await test("mac-audition-playback-failure-disposes-and-requires-new-choice", async () =>
        {
            var first = new Playback(); var second = new Playback(); var queue = new Queue<Playback>([first, second]);
            await using var player = new MacCalibrationAuditionPlayer(() => Devices(), () => queue.Dequeue());
            var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.Failed += () => failure.TrySetResult();
            var oldToken = player.GetOutputs()[0].Token;
            await player.SelectOutputAsync(oldToken, default); await player.PrepareAsync(1, default); await player.PlayAsync(default);
            first.Fail(); await failure.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(first.DisposeCount == 1 && player.SelectedOutputToken is null, "Failure releases the native queue before notifying and clears selection.");
            await Throws<InvalidOperationException>(() => player.PrepareAsync(1, default));
            await Throws<InvalidOperationException>(() => player.PlayAsync(default));
            await Throws<InvalidOperationException>(() => player.SelectOutputAsync(oldToken, default));
            var freshToken = player.GetOutputs()[0].Token;
            Check(freshToken != oldToken, "Failure invalidates output tokens.");
            await player.SelectOutputAsync(freshToken, default); await player.PrepareAsync(1, default);
            Check(second.PreparedId == "uid-private-a" && second.PlayCount == 0, "Reconfirmation prepares a fresh queue without automatic playback.");
        });
        await test("mac-audition-reset-active-stream-disposes-and-invalidates", async () =>
        {
            var playback = new Playback();
            await using var player = new MacCalibrationAuditionPlayer(() => Devices(), () => playback);
            var token = player.GetOutputs()[0].Token;
            await player.SelectOutputAsync(token, default); await player.PrepareAsync(1, default); await player.PlayAsync(default);
            player.ResetSelection();
            Check(playback.DisposeCount == 1 && player.SelectedOutputToken is null, "Reset stops the active routed stream.");
            await Throws<InvalidOperationException>(() => player.PlayAsync(default));
            await Throws<InvalidOperationException>(() => player.PrepareAsync(1, default));
            await Throws<InvalidOperationException>(() => player.SelectOutputAsync(token, default));
        });
        await test("mac-audition-prepare-failure-cleans-unpublished-queue", async () =>
        {
            var playback = new Playback { FailPrepare = true };
            await using var player = new MacCalibrationAuditionPlayer(() => Devices(), () => playback);
            await player.SelectOutputAsync(player.GetOutputs()[0].Token, default);
            await Throws<IOException>(() => player.PrepareAsync(1, default));
            Check(playback.DisposeCount == 1 && playback.PlayCount == 0, "Failed queue preparation cannot leave a live stream.");
        });
        await test("mac-audition-spectrum-from-embedded-pcm-and-clean-stop", async () =>
        {
            var playback = new Playback(); int failures = 0;
            await using var player = new MacCalibrationAuditionPlayer(() => Devices(), () => playback);
            var spectrum = new TaskCompletionSource<IReadOnlyList<double>>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.Spectrum += levels => spectrum.TrySetResult(levels); player.Failed += () => failures++;
            await player.SelectOutputAsync(player.GetOutputs()[0].Token, default);
            await player.PrepareAsync(1, default); await player.PlayAsync(default);
            var levels = await spectrum.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(levels.Count > 0 && levels.All(double.IsFinite), "Offline PCM produces finite spectrum levels.");
            await player.StopAsync(); await Task.Delay(150);
            Check(failures == 0 && playback.DisposeCount == 1, "Normal cancellation and stop do not emit a device failure.");
        });
#else
        await Task.CompletedTask;
#endif
    }
#if HEYMELODY_MACOS
    private static IReadOnlyList<AudioOutputDevice> Devices() => [new("uid-private-a", "Earbuds"), new("uid-private-b", "Earbuds")];
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private sealed class Playback : IAudioPlayback
    {
        private int _failed;
        public string? PreparedId { get; private set; }
        public uint SampleRate { get; private set; }
        public int SampleCount { get; private set; }
        public int PlayCount { get; private set; }
        public int PauseCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool FailPrepare { get; init; }
        public bool HasFailed => Volatile.Read(ref _failed) != 0;
        public long PlaybackFrame => 1024;
        public void Fail() => Volatile.Write(ref _failed, 1);
        public void Prepare(string deviceId, short[] samples, uint sampleRate)
        {
            if (FailPrepare) throw new IOException("Injected prepare failure.");
            PreparedId = deviceId; SampleRate = sampleRate; SampleCount = samples.Length;
        }
        public void Play() { ObjectDisposedException.ThrowIf(DisposeCount != 0, this); PlayCount++; }
        public void Pause() { ObjectDisposedException.ThrowIf(DisposeCount != 0, this); PauseCount++; }
        public void Dispose() => DisposeCount++;
    }
#endif
}
