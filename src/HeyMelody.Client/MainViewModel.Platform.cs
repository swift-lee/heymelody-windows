namespace HeyMelodyWindows.App;

public sealed partial class MainViewModel
{
    public UiPlatformStatus? PlatformStatus { get; private set; }
    public string PlatformMessage => HasPlatformWarning ? PlatformStatus!.Message : "";
    public bool HasPlatformWarning => !IsDemoSelected && PlatformStatus is { Message.Length: > 0 } status
        && (status.BluetoothAccess is "NotDetermined" or "Denied" or "Restricted" or "Unsupported"
            || status.AdapterState == "PoweredOff");

    private void PresentPlatformStatus(UiPlatformStatus? status)
    {
        PlatformStatus = status;
        NotifyState();
    }
}
