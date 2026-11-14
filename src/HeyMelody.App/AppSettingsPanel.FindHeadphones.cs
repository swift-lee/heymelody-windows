using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace HeyMelodyWindows.App;

public partial class AppSettingsPanel
{
    private readonly CancellationTokenSource _findHeadphonesCancellation = new();
    private bool _findHeadphonesDraft, _findHeadphonesSaved, _presentingFindHeadphones;
    private bool _checkingLocationPermission, _savingFindHeadphones;
    private string? _locationPermissionStatus;
    internal event Action? FindHeadphonesBusyChanged;
    internal bool FindHeadphonesBusy => _checkingLocationPermission || _savingFindHeadphones;
    internal bool FindHeadphonesDraft => _findHeadphonesDraft;

    private void InitializeFindHeadphonesSettings()
    {
        _findHeadphonesDraft = _findHeadphonesSaved = _viewModel?.FindHeadphonesEnabled == true;
        _locationPermissionStatus = _viewModel?.FindHeadphonesPermissionStatus;
        FindHeadphonesSwitch.PropertyChanged += async (_, change) =>
        {
            if (_presentingFindHeadphones || change.Property != ToggleButton.IsCheckedProperty) return;
            if (FindHeadphonesBusy) { PresentFindHeadphonesSettings(); return; }
            if (FindHeadphonesSwitch.IsChecked == true)
                await CheckFindHeadphonesPermissionAsync();
            else
            {
                _findHeadphonesDraft = false;
                ShowLocationPermissionMessage("");
                PresentFindHeadphonesSettings();
            }
        };
        PresentFindHeadphonesSettings();
    }

    private void PresentFindHeadphonesSettings()
    {
        _presentingFindHeadphones = true;
        try
        {
            FindHeadphonesSwitch.IsChecked = _findHeadphonesDraft;
            FindHeadphonesSwitch.IsEnabled = _viewModel is { FindHeadphonesServiceAvailable: true, Closing: false }
                && !FindHeadphonesBusy && !_findHeadphonesCancellation.IsCancellationRequested;
            LocationPermissionProgress.IsVisible = FindHeadphonesBusy;
            FindHeadphonesSettingsCard.Description = _checkingLocationPermission ? "正在向系统检查定位权限…"
                : _savingFindHeadphones ? "正在保存位置记录设置…"
                : _viewModel?.FindHeadphonesServiceAvailable != true ? "定位记录服务暂不可用，请检查后台连接后重新打开设置。"
                : "记录此电脑的位置作为查找参考；还需为每个设备单独开启。";
        }
        finally { _presentingFindHeadphones = false; }
        FindHeadphonesBusyChanged?.Invoke();
    }

    internal async Task CheckFindHeadphonesPermissionAsync()
    {
        if (FindHeadphonesBusy || _viewModel is not { } vm || _findHeadphonesCancellation.IsCancellationRequested) return;
        _checkingLocationPermission = true;
        _findHeadphonesDraft = false;
        ShowLocationPermissionMessage("");
        PresentFindHeadphonesSettings();
        try
        {
            var permission = await vm.RequestFindHeadphonesPermissionAsync(_findHeadphonesCancellation.Token);
            if (_findHeadphonesCancellation.IsCancellationRequested) return;
            _locationPermissionStatus = permission.Status;
            _findHeadphonesDraft = permission.Status == "allowed";
            ShowLocationPermissionMessage(_findHeadphonesDraft ? "" : permission.Message.Length != 0
                ? permission.Message : "定位权限检查未通过，请在系统设置中检查定位权限。");
        }
        catch (OperationCanceledException) when (_findHeadphonesCancellation.IsCancellationRequested) { }
        catch
        {
            if (!_findHeadphonesCancellation.IsCancellationRequested)
                ShowLocationPermissionMessage("无法完成定位权限检查，请重试。");
        }
        finally
        {
            _checkingLocationPermission = false;
            PresentFindHeadphonesSettings();
        }
    }

    internal async Task<bool> SaveFindHeadphonesDraftAsync(CancellationToken token)
    {
        if (FindHeadphonesBusy) return false;
        if (_findHeadphonesDraft == _findHeadphonesSaved) return true;
        if (_viewModel is not { } vm) return false;
        _savingFindHeadphones = true;
        ShowLocationPermissionMessage("");
        PresentFindHeadphonesSettings();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _findHeadphonesCancellation.Token);
        try
        {
            await vm.SaveFindHeadphonesEnabledAsync(_findHeadphonesDraft, linked.Token);
            _findHeadphonesSaved = _findHeadphonesDraft;
            return true;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return false; }
        catch (Exception error)
        {
            _locationPermissionStatus = vm.FindHeadphonesPermissionStatus;
            ShowLocationPermissionMessage(error is InvalidOperationException ? error.Message
                : "无法保存位置记录设置，请检查后台连接后重试。");
            return false;
        }
        finally { _savingFindHeadphones = false; PresentFindHeadphonesSettings(); }
    }

    internal void CancelFindHeadphonesOperations() => _findHeadphonesCancellation.Cancel();

    private void ShowLocationPermissionMessage(string message)
    {
        LocationPermissionResult.Message = message;
        LocationPermissionResult.IsOpen = LocationPermissionResult.IsVisible = message.Length != 0;
        LocationSystemSettingsButton.IsVisible = message.Length != 0
            && _locationPermissionStatus is "denied" or "systemDisabled" or "restricted";
    }

    private async void LocationSystemSettings_Click(object? sender, RoutedEventArgs e)
    {
        var uri = OperatingSystem.IsWindows() ? new Uri("ms-settings:privacy-location")
            : OperatingSystem.IsMacOS()
                ? new Uri("x-apple.systempreferences:com.apple.preference.security?Privacy_LocationServices") : null;
        if (uri is null || Avalonia.Controls.TopLevel.GetTopLevel(this) is not { } owner) return;
        try
        {
            if (!await owner.Launcher.LaunchUriAsync(uri))
                ShowLocationPermissionMessage("无法打开系统定位设置，请在系统设置的隐私与安全性中检查定位服务。");
        }
        catch { ShowLocationPermissionMessage("无法打开系统定位设置，请在系统设置的隐私与安全性中检查定位服务。"); }
    }
}
