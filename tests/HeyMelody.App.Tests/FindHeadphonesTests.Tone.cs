using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

internal static partial class FindHeadphonesTests
{
    private static async Task ToneDialogAsync()
    {
        var service = new LocationService { ToneEnabled = true };
        var window = new Window { Width = 1280, Height = 900 };
        await using var vm = new MainViewModel(service, new WindowConfirmationService(window));
        var view = new MainView { DataContext = vm };
        window.Content = view;
        window.Show();
        try
        {
            await vm.InitializeAsync();
            view.FindControl<FASettingsExpander>("FindHeadphonesPanel")!.IsExpanded = true;
            var button = view.FindControl<Button>("PlayFindHeadphonesButton")!;
            Dispatcher.UIThread.RunJobs();
            Check(button.IsEnabled && !vm.FindHeadphonesEnabled, "Recording policy does not gate tone playback.");

            var warning = await PlayDialogAsync();
            Check(warning.DefaultButton == FAContentDialogButton.Close && warning.PrimaryButtonText == "我已取下，播放"
                && warning.CloseButtonText == "取消" && WarningText(warning).Contains("听力受损"), "An explicit, non-default acknowledgement is required.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.GetVisualDescendants().OfType<FAContentDialog>().Count() == 1 && service.ToneCalls.Count == 0,
                "Repeated clicks cannot stack confirmations or submit playback.");
            ClickDialogButton(window, warning, "取消");
            await PumpUntilAsync(() => button.IsEnabled);
            Check(service.ToneCalls.Count == 0, "Cancelling a warning issues no command.");

            _ = await PlayDialogAsync();
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await PumpUntilAsync(() => button.IsEnabled);
            Check(service.ToneCalls.Count == 0, "Escape issues no command.");

            _ = await PlayDialogAsync();
            vm.AdoptBackendEvent(ToneEvent(service.ToneSnapshot("unknown-a", worn: true)));
            await PumpUntilAsync(() => button.IsEnabled);
            Check(service.ToneCalls.Count == 0, "Newly detected wear invalidates an ordinary confirmation.");
            warning = await PlayDialogAsync();
            Check(WarningText(warning).Contains("耳机报告仍在佩戴"), "An in-ear report changes the confirmation copy.");
            ClickDialogButton(window, warning, "我已取下，播放");
            await PumpUntilAsync(() => button.IsEnabled && Equals(button.Content, "停止"));
            Check(service.ToneCalls.Single().Args is { Play: true, Confirmed: true, ConfirmedWorn: true },
                "Only the explicit worn confirmation grants the worn override.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await PumpUntilAsync(() => button.IsEnabled && Equals(button.Content, "播放"));
            Check(service.ToneCalls.Count == 2 && service.ToneCalls[^1].Args.Play == false, "Stop does not ask to play again.");

            vm.AdoptBackendEvent(ToneEvent(service.ToneSnapshot("unknown-a", worn: false)));
            _ = await PlayDialogAsync();
            var devices = await service.EnumerateAsync(default);
            await vm.SelectDeviceAsync(devices[1]);
            await PumpUntilAsync(() => button.IsEnabled);
            Check(service.ToneCalls.Count == 2 && vm.SelectedDevice?.Identity == "unknown-b", "Switch invalidates the frozen confirmation.");

            _ = await PlayDialogAsync();
            service.ToneBackend = "replacement-backend";
            vm.AdoptBackendEvent(ToneEvent(service.ToneSnapshot("unknown-b")));
            await PumpUntilAsync(() => button.IsEnabled);
            Check(service.ToneCalls.Count == 2, "A new backend cannot inherit a pending confirmation.");

            warning = await PlayDialogAsync();
            ClickDialogButton(window, warning, "我已取下，播放");
            await PumpUntilAsync(() => button.IsEnabled && Equals(button.Content, "停止"));
            await vm.SelectDeviceAsync(devices[0]);
            Check(service.ToneCalls.Count == 4 && service.ToneCalls[^2] is { Target.BackendInstanceId: "replacement-backend",
                Target.Identity: "unknown-b", Args.ConfirmedWorn: false }
                && service.ToneCalls[^1] is { Target.Identity: "unknown-b", Args.Play: false },
                "Switching while ringing stops only the original selected device.");

            warning = await PlayDialogAsync();
            ClickDialogButton(window, warning, "我已取下，播放");
            await PumpUntilAsync(() => button.IsEnabled && Equals(button.Content, "停止"));
            vm.AdoptBackendEvent(new() { Kind = "unavailable", BackendInstanceId = service.ToneBackend });
            Check(!vm.CanToggleFindHeadphonesTone && vm.FindHeadphonesToneDescription.Contains("未确认") && service.ToneCalls.Count == 5,
                "Losing the backend during playback warns that silence is unconfirmed, without sending or replaying commands.");

            async Task<FAContentDialog> PlayDialogAsync()
            {
                await PumpUntilAsync(() => !window.GetVisualDescendants().OfType<FAContentDialog>().Any());
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return await CapturedToneDialogAsync(window);
            }
        }
        finally { window.Close(); }
    }

    private static async Task<FAContentDialog> CapturedToneDialogAsync(Window window)
    {
        const string expectedTitle = "⚠️警告：可能会造成听力受损！";
        await PumpUntilAsync(() => window.GetVisualDescendants().OfType<FAContentDialog>()
            .Any(dialog => Equals(dialog.Title, expectedTitle)));
        return window.GetVisualDescendants().OfType<FAContentDialog>().Single(dialog => Equals(dialog.Title, expectedTitle));
    }

    private static string WarningText(FAContentDialog dialog) => ((TextBlock)dialog.Content!).Text!;

    private static BackendEvent ToneEvent(UiSnapshot snapshot) =>
        new() { Kind = "snapshot", BackendInstanceId = snapshot.BackendInstanceId, Snapshot = snapshot };

    private sealed partial class LocationService
    {
        internal bool ToneEnabled;
        internal string ToneBackend = "location-backend";
        private long _toneRevision;
        private readonly Dictionary<string, UiFindHeadphonesToneState> _toneStates = new();
        internal readonly List<(FindHeadphonesToneTarget Target, FindHeadphonesToneArgs Args)> ToneCalls = [];

        internal UiSnapshot ToneSnapshot(string identity, bool? worn = null)
        {
            var state = _toneStates.GetValueOrDefault(identity) ?? new(1, FindHeadphonesTonePhase.Idle, true, false);
            if (worn is { } value) state = state with { Worn = value };
            _toneStates[identity] = state;
            return new()
            {
                Identity = identity, DeviceName = identity, BackendInstanceId = ToneBackend, ProductId = "06EC10",
                Connected = true, ControlReady = true, SupportedModel = true, DeviceGeneration = 1,
                Revision = ++_toneRevision, FindHeadphonesTone = state, ConnectionState = ConnectionState.Connected
            };
        }

        public Task<UiFindHeadphonesToneResult> SetFindHeadphonesToneAsync(FindHeadphonesToneTarget target, bool play,
            bool confirmed, bool confirmedWorn, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(target.BackendInstanceId == ToneBackend && target.DeviceGeneration == 1 && (confirmed || !play),
                "The fixture must only receive confirmed commands for its exact backend and generation.");
            ToneCalls.Add((target, new(target.Session, play, confirmed, confirmedWorn)));
            _toneStates[target.Identity] = _toneStates[target.Identity] with
            {
                Phase = play ? FindHeadphonesTonePhase.Ringing : FindHeadphonesTonePhase.Idle,
                CanStart = !play
            };
            return Task.FromResult(new UiFindHeadphonesToneResult(true, 0, "", ToneSnapshot(target.Identity)));
        }
    }
}
