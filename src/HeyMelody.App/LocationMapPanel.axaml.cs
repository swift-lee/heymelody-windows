using Avalonia.Controls;

namespace HeyMelodyWindows.App;

public partial class LocationMapPanel : UserControl
{
    public LocationMapPanel() => InitializeComponent();

    internal string SelectedProvider
    {
        get => Choices().FirstOrDefault(choice => choice.IsChecked == true)?.Tag as string ?? "openstreetmap";
        set
        {
            var provider = value is "apple" or "google" or "amap" ? value : "openstreetmap";
            foreach (var choice in Choices()) choice.IsChecked = Equals(choice.Tag, provider);
        }
    }

    private RadioButton[] Choices() => [LocationMapOsm, LocationMapApple, LocationMapGoogle, LocationMapAmap];

    internal void ShowError(string message)
    {
        LocationMapError.Message = message;
        LocationMapError.IsOpen = LocationMapError.IsVisible = message.Length != 0;
    }
}
