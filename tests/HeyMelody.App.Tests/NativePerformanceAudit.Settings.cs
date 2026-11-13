using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelody.Diagnostics;
using HeyMelodyWindows.App;

internal static partial class NativePerformanceAudit
{
    private static async Task VerifySettingsAfterMeasurementAsync(MainWindow window, MainView page,
        DeviceModelView? model, DeviceAnimatedWebpView? animation)
    {
        if (_state.MeasurementEndedUtc is null)
            throw new InvalidOperationException("Settings verification must follow the completed baseline measurement.");
        var baselineCounts = (_state.Callbacks.Count, _state.Samples.Count, _state.FrameCopies.Count);
        var startedAtUtc = DateTime.UtcNow;
        var modelFramesBefore = model?.PresentedFrameCount;
        var animationDrawsBefore = animation?.RenderedFrameCount;
        var artwork = model is not null ? "free4-dynaudio-native-filament"
            : _state.Artwork is null ? "x4-two-frame-webp-fixture" : "explicit-local-animated-webp";
        var artworkSha256 = model?.ModelDigest
            ?? JsonSerializer.SerializeToElement(_state.ArtworkDescription).GetProperty("sha256").GetString();
        using var timing = new NativeSettingsTimingProbe(window);
        Exception? failure = null;
        try
        {
            RequireHealthyArtwork();
            foreach (var visit in new[] { "first", "warm-1", "warm-2" })
            {
                var opening = timing.BeginOpen(_state.Theme, artwork, visit);
                var operation = page.OpenSettingsDialogAsync();
                await UntilSettingsAsync(() => FindSettingsDialog() is not null, "Application settings did not open.");
                await opening.WaitAsync(TimeSpan.FromSeconds(5));
                var dialog = FindSettingsDialog()
                    ?? throw new InvalidOperationException("Application settings disappeared before cancellation.");
                var cancel = dialog.GetVisualDescendants().OfType<Button>()
                    .Single(button => Equals(button.Content, "取消"));
                await ClickSettingsCancelAsync(window, cancel);
                await operation.WaitAsync(TimeSpan.FromSeconds(5));
                await UntilSettingsAsync(() => FindSettingsDialog() is null, "Application settings did not close.");
                RequireHealthyArtwork();
            }
            timing.AssertIdentitySamples();
            await UntilSettingsAsync(() => model is not null
                    ? model.PresentedFrameCount > modelFramesBefore
                    : animation!.RenderedFrameCount > animationDrawsBefore,
                "Artwork did not continue presenting around the settings interactions.");
            if (baselineCounts != (_state.Callbacks.Count, _state.Samples.Count, _state.FrameCopies.Count))
                throw new InvalidOperationException("Settings verification changed the completed baseline sample counts.");
            if (File.Exists(ThemePreferences.SettingsPathOverride) || File.Exists(DiagnosticPaths.SettingsFile))
                throw new InvalidOperationException("Cancelling settings unexpectedly wrote isolated preferences.");
            _state.SettingsVerificationCompleted = true;
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            timing.Dispose();
            string? cleanupFailure = null;
            if (!_state.SettingsVerificationCompleted)
            {
                try { await page.DismissSettingsDialogAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception cleanupError) { cleanupFailure = cleanupError.ToString(); }
            }
            _state.SettingsVerification = new
            {
                schemaVersion = 1, success = _state.SettingsVerificationCompleted,
                settingsVerificationStarted = true, theme = _state.Theme, artworkSha256,
                artwork = _state.ArtworkDescription,
                startedAtUtc, endedAtUtc = DateTime.UtcNow, baselineMeasurementEndedUtc = _state.MeasurementEndedUtc,
                modelFramesBefore, modelFramesAfter = model?.PresentedFrameCount,
                animationDrawsBefore, animationDrawsAfter = animation?.RenderedFrameCount,
                settingsTiming = timing.Receipts, settingsLifecycle = timing.LifecycleReceipts,
                nativeBluetoothCalls = 0, productionBackendStarted = false,
                forbiddenServiceCalls = _state.Service.ForbiddenCalls,
                isolatedPreferencesWritten = File.Exists(ThemePreferences.SettingsPathOverride),
                isolatedDiagnosticSettingsWritten = File.Exists(DiagnosticPaths.SettingsFile),
                failure = failure?.ToString(), cleanupFailure,
                scope = "Three open/cancel cycles after the baseline measurement with a fake service. "
                    + "Artwork identity and SHA256 are recorded above; an explicit file is not automatically a full X4 animation. "
                    + "Native model uses real Filament. RAF and composition-batch completion are not displayed-frame or GPU timings."
            };
        }

        FAContentDialog? FindSettingsDialog() => window.GetVisualDescendants().OfType<FAContentDialog>()
            .SingleOrDefault(dialog => Equals(dialog.Title, "应用设置"));

        void RequireHealthyArtwork()
        {
            if (model is not null) RequireHealthyModel(model, model.Diagnostics);
            else if (animation is not null) RequireHealthyAnimation(animation);
            else throw new InvalidOperationException("Settings verification requires a model or animated artwork.");
        }
    }

    private static async Task UntilSettingsAsync(Func<bool> condition, string failure)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(failure);
            await Task.Delay(20);
        }
    }

    private static async Task ClickSettingsCancelAsync(Window window, Button cancel)
    {
        cancel.BringIntoView();
        await Task.Delay(40);
        var point = NativeWindowCapture.ClientPoint(window.TryGetPlatformHandle()!.Handle,
            cancel.PointToScreen(new(cancel.Bounds.Width / 2, cancel.Bounds.Height / 2)));
        var hwnd = window.TryGetPlatformHandle()!.Handle;
        NativeWindowCapture.PointerMessage(hwnd, 0x0200, point.X, point.Y, false);
        await Task.Delay(30);
        NativeWindowCapture.PointerMessage(hwnd, 0x0201, point.X, point.Y, true);
        await Task.Delay(30);
        NativeWindowCapture.PointerMessage(hwnd, 0x0202, point.X, point.Y, false);
    }
}
