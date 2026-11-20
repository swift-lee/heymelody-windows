using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;

namespace HeyMelodyWindows.App;

public partial class MainView
{
    private bool _copyingLocation, _locationCopied, _openingLocationMap;
    private long _locationActionVersion;
    private DispatcherTimer? _locationCopyFeedbackTimer;
    private Task? _mapOperation;
    private CancellationTokenSource? _mapCancellation;
    private FAContentDialog? _mapDialog;

    private async void PlayFindHeadphones_Click(object? sender, RoutedEventArgs e)
    {
        if (!_closingWindow && ViewModel is { } vm) await vm.ToggleFindHeadphonesToneAsync();
    }

    private void InitializeFindHeadphones()
    {
        DeviceLocationRecordingSwitch.PropertyChanged += async (_, change) =>
        {
            if (change.Property != ToggleButton.IsCheckedProperty || ViewModel is not { CanSetDeviceLocationRecording: true } vm
                || DeviceLocationRecordingSwitch.IsChecked == vm.DeviceLocationRecordingEnabled) return;
            await vm.SetDeviceLocationRecordingAsync(DeviceLocationRecordingSwitch.IsChecked == true);
            DeviceLocationRecordingSwitch.SetCurrentValue(ToggleButton.IsCheckedProperty, ViewModel?.DeviceLocationRecordingEnabled == true);
        };
        DetachedFromVisualTree += (_, _) => ResetLocationActions();
    }

    private void ResetLocationActions()
    {
        _locationActionVersion++;
        _copyingLocation = false;
        ResetLocationCopyFeedback();
        _mapCancellation?.Cancel();
        _mapDialog?.Hide();
    }

    private async void CopyLocation_Click(object? sender, RoutedEventArgs e) => await CopyLocationAsync();

    internal async Task CopyLocationAsync()
    {
        UpdateLocationCopyButton();
        if (_copyingLocation || _closingWindow || ViewModel is not { HasRecordedLocation: true, Closing: false } vm) return;
        var version = _locationActionVersion;
        var text = vm.LocationGeoUri;
        ResetLocationCopyFeedback();
        _copyingLocation = true;
        try
        {
            if (WriteClipboardOverride is { } write) await write(text);
            else if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
            else throw new InvalidOperationException("Clipboard unavailable.");
            if (version == _locationActionVersion && ReferenceEquals(vm, ViewModel) && !_closingWindow && !vm.Closing)
                ShowLocationCopied();
        }
        catch
        {
            if (version == _locationActionVersion && ReferenceEquals(vm, ViewModel) && !_closingWindow && !vm.Closing)
                ResetLocationCopyFeedback();
        }
        finally
        {
            if (version == _locationActionVersion) _copyingLocation = false;
        }
    }

    private void UpdateLocationCopyButton()
    {
        CopyLocationButton.SetCurrentValue(ToggleButton.IsCheckedProperty, _locationCopied);
        CopyLocationButton.Content = _locationCopied ? "已复制" : "复制坐标";
    }

    private void ResetLocationCopyFeedback()
    {
        _locationCopyFeedbackTimer?.Stop();
        _locationCopyFeedbackTimer = null;
        _locationCopied = false;
        UpdateLocationCopyButton();
    }

    private void ShowLocationCopied()
    {
        ResetLocationCopyFeedback();
        _locationCopied = true;
        UpdateLocationCopyButton();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _locationCopyFeedbackTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_locationCopyFeedbackTimer, timer)) ResetLocationCopyFeedback();
        };
        timer.Start();
    }

    private async void OpenLocationMap_Click(object? sender, RoutedEventArgs e) => await OpenLocationMapDialogAsync();

    internal async Task OpenLocationMapDialogAsync()
    {
        if (_mapOperation is not null || _settingsOperation is not null || _closingWindow
            || ViewModel is not { Closing: false, RecordedLocation: { } location, SelectedDevice: { } selected } vm) return;
        using var cancellation = new CancellationTokenSource();
        _mapCancellation = cancellation;
        _mapOperation = vm.Dialogs.RunAsync(token => ShowLocationMapDialogAsync(vm, selected.Identity, location, token), cancellation.Token);
        try { await _mapOperation; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || vm.Closing) { }
        catch { return; }
        finally { cancellation.Cancel(); _mapOperation = null; _mapCancellation = null; _mapDialog = null; }
    }

    private async Task ShowLocationMapDialogAsync(MainViewModel vm, string identity, UiRecordedLocation location, CancellationToken token)
    {
        if (_closingWindow || vm.Closing || vm.SelectedDevice?.Identity != identity || TopLevel.GetTopLevel(this) is not { } owner) return;
        token.ThrowIfCancellationRequested();
        var panel = new LocationMapPanel { SelectedProvider = ThemePreferences.LoadMapProvider() };
        var dialog = new FAContentDialog
        {
            Title = "在地图上查看", Content = panel, PrimaryButtonText = "打开", CloseButtonText = "取消",
            DefaultButton = FAContentDialogButton.Primary
        };
        _mapDialog = dialog;
        KeepSettingsDialogUnscaled(dialog);
        Border? surface = null;
        // The library's Container fills the window; BackgroundElement is the visible dialog surface.
        dialog.TemplateApplied += (_, args) => surface = args.NameScope.Find<Border>("BackgroundElement");
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (_openingLocationMap) { args.Cancel = true; return; }
            var deferral = args.GetDeferral();
            _openingLocationMap = true;
            dialog.IsPrimaryButtonEnabled = false;
            panel.ShowError("");
            try
            {
                if (token.IsCancellationRequested || _closingWindow || vm.Closing || vm.SelectedDevice?.Identity != identity)
                { args.Cancel = true; return; }
                var provider = panel.SelectedProvider;
                var saved = ThemePreferences.SaveMapProvider(provider);
                var launched = await (LauncherOverride ?? owner.Launcher).LaunchUriAsync(CreateLocationMapUri(provider, location)).WaitAsync(token);
                if (token.IsCancellationRequested || _closingWindow || vm.Closing || !ReferenceEquals(vm, ViewModel)
                    || vm.SelectedDevice?.Identity != identity) { args.Cancel = true; return; }
                if (!launched)
                {
                    args.Cancel = true;
                    panel.ShowError("无法打开地图，请重试或取消后复制坐标。" + (saved ? "" : "地图选择也未保存。"));
                }
            }
            catch
            {
                args.Cancel = true;
                if (!token.IsCancellationRequested && !_closingWindow)
                    panel.ShowError("无法打开地图，请重试或取消后复制坐标。");
            }
            finally
            {
                _openingLocationMap = false;
                dialog.IsPrimaryButtonEnabled = true;
                deferral.Complete();
                if (token.IsCancellationRequested) dialog.Hide();
            }
        };
        dialog.CloseButtonClick += (_, _) => _mapCancellation?.Cancel();
        void DismissOnOutsidePress(object? sender, PointerPressedEventArgs args)
        {
            if (surface is not { IsVisible: true, Bounds.Width: > 0, Bounds.Height: > 0 }
                || new Rect(surface.Bounds.Size).Contains(args.GetPosition(surface))) return;
            // Consume the press before the covered page can act on it.
            args.Handled = true;
            _mapCancellation?.Cancel();
            dialog.Hide();
        }
        owner.AddHandler(InputElement.PointerPressedEvent, DismissOnOutsidePress, RoutingStrategies.Tunnel, handledEventsToo: true);
        try { await ShowSettingsDialogAsync(dialog, owner, token); }
        finally { owner.RemoveHandler(InputElement.PointerPressedEvent, DismissOnOutsidePress); }
    }

    private async Task DismissLocationMapAsync()
    {
        _mapCancellation?.Cancel();
        _mapDialog?.Hide();
        if (_mapOperation is { } operation)
            try { await operation; }
            catch (OperationCanceledException) { }
    }

    internal static Uri CreateLocationMapUri(string provider, UiRecordedLocation location)
    {
        var latitude = location.Latitude.ToString("0.########", CultureInfo.InvariantCulture);
        var longitude = location.Longitude.ToString("0.########", CultureInfo.InvariantCulture);
        return new Uri(provider switch
        {
            "apple" => $"https://maps.apple.com/?ll={latitude},{longitude}&q={Uri.EscapeDataString("耳机断开位置")}",
            "google" => $"https://www.google.com/maps/search/?api=1&query={latitude},{longitude}",
            "amap" => $"https://uri.amap.com/marker?position={longitude},{latitude}&coordinate=wgs84",
            _ => $"https://www.openstreetmap.org/?mlat={latitude}&mlon={longitude}#map=17/{latitude}/{longitude}"
        });
    }
}
