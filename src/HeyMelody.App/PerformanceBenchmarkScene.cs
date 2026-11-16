using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace HeyMelodyWindows.App;

/// <summary>The same product-window geometry and artwork scene in published and test audits.</summary>
internal static class PerformanceBenchmarkScene
{
    internal static MainWindow Create(IHeadsetService service, bool model, bool integrated = false)
    {
        var window = new MainWindow(service, preview: !integrated)
        {
            StartWithDemo = !integrated, Width = 1280, Height = 900, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowActivated = true
        };
        var page = window.FindControl<MainView>("Page")!;
        if (!integrated)
        {
            if (!model) page.FindControl<ProductArtwork>("HeroArt")!.VisualTemplate = new StaticArtworkTemplate();
            page.SetModelDemo();
        }
        return window;
    }

    private sealed class StaticArtworkTemplate : IDataTemplate
    {
        public bool Match(object? data) => true;
        public Control? Build(object? data) => DeviceVisuals.DefaultTemplate.Build(DeviceVisuals.X4Product);
    }
}
