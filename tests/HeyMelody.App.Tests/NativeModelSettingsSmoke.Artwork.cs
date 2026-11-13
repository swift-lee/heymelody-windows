using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

internal static partial class NativeModelSettingsSmoke
{
    private static async Task VerifyArtworkSettingsAsync(Window window, MainView page, NativeSettingsTimingProbe timing)
    {
        var hero = page.FindControl<ProductArtwork>("HeroArt")!;
        hero.VisualTemplate = new SettingsArtworkTemplate(() => new EncoFree4ModelView());
        EncoFree4ModelView? free4 = null;
        await UntilAsync(() =>
        {
            free4 = hero.GetVisualDescendants().OfType<EncoFree4ModelView>().SingleOrDefault();
            return free4 is { ModelReady: true, LoadError: null };
        }, "The ordinary Free4 model is ready through the managed recording renderer.");
        var beforeFrames = free4!.PresentedFrameCount;
        var beforeCreations = _state.Factory.CreateCalls;
        await CancelArtworkSettingsAsync(window, page, timing, "free4-managed");
        Check(free4.ModelReady && free4.LoadError is null && _state.Factory.CreateCalls == beforeCreations
            && ReferenceEquals(free4, hero.GetVisualDescendants().OfType<EncoFree4ModelView>().SingleOrDefault()),
            "The ordinary Free4 model retains its ready instance without renderer recreation around settings.");
        _state.ArtworkReceipts.Add(new
        {
            kind = "free4-managed", framesBefore = beforeFrames, framesAfter = free4.PresentedFrameCount,
            rendererCreations = beforeCreations,
            scope = "Bundled ordinary Free4 GLB with a managed recording renderer; readiness and lifecycle only, not native frame progress or hardware performance."
        });

        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "x4-first-two-frames.webp");
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        hero.VisualTemplate = new SettingsArtworkTemplate(() => new DeviceAnimatedWebpView(
            new(new ArtworkFile(new Uri(path), Path.GetDirectoryName(path)!, digest), DeviceVisuals.X4Product)));
        DeviceAnimatedWebpView? animation = null;
        await UntilAsync(() =>
        {
            animation = hero.GetVisualDescendants().OfType<DeviceAnimatedWebpView>().SingleOrDefault();
            return animation is { AnimationLoaded: true, LoadError: null, RenderedFrameCount: > 0 };
        }, "The two-frame X4 WebP fixture loads in the native fake-service window.");
        var beforeDraws = animation!.RenderedFrameCount;
        await CancelArtworkSettingsAsync(window, page, timing, "x4-two-frame-webp-fixture");
        Check(animation.AnimationLoaded && animation.LoadError is null && animation.RenderedFrameCount > beforeDraws,
            "The X4 WebP fixture continues drawing around an application settings interaction.");
        _state.ArtworkReceipts.Add(new
        {
            kind = "x4-two-frame-webp-fixture", sha256 = digest, drawsBefore = beforeDraws,
            drawsAfter = animation.RenderedFrameCount,
            scope = "Two-frame test fixture and fake service; not a complete production X4 animation or physical-device verification."
        });
    }

    private static async Task CancelArtworkSettingsAsync(Window window, MainView page,
        NativeSettingsTimingProbe timing, string artwork)
    {
        var opening = timing.BeginOpen("dark", artwork, "artwork-validation");
        var operation = page.OpenSettingsDialogAsync();
        var dialog = await SettingsAsync(window, opening);
        await ClickAsync(window, dialog.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "取消")));
        await operation.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private sealed class SettingsArtworkTemplate(Func<Control> create) : IDataTemplate
    {
        public bool Match(object? data) => true;
        public Control Build(object? data) => create();
    }
}
