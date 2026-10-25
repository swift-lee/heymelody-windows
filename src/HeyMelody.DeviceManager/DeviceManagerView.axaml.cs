using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HeyMelody.Platform;
using HeyMelodyWindows.Ui;

namespace HeyMelodyWindows.DeviceManager;

public partial class DeviceManagerView : UserControl
{
    private bool _openingSettings;
    internal ILauncher? LauncherOverride { get; set; }
    public DeviceManagerView()
    {
        InitializeComponent();
        SmoothScrolling.Attach(DeviceScroll);
        BluetoothSettingsHost.IsEnabled = DesktopSystemSettings.BluetoothUri is not null;
    }
    private async void BluetoothSettings_Click(object? sender, RoutedEventArgs e) => await OpenBluetoothSettingsAsync();
    internal async Task OpenBluetoothSettingsAsync()
    {
        if (_openingSettings || DesktopSystemSettings.BluetoothUri is not { } settingsUri
            || DataContext is not DeviceManagerViewModel { CanRefresh: true } vm
            || TopLevel.GetTopLevel(this) is not { } owner) return;
        _openingSettings = true;
        BluetoothSettingsHost.IsEnabled = false;
        try
        {
            var accepted = false;
            try { accepted = await (LauncherOverride ?? owner.Launcher).LaunchUriAsync(settingsUri); }
            catch (Exception) { }
            if (!accepted && !vm.Closing) vm.ReportLaunchFailure();
        }
        finally
        {
            _openingSettings = false;
            BluetoothSettingsHost.IsEnabled = !vm.Closing && DesktopSystemSettings.BluetoothUri is not null;
        }
    }
}
