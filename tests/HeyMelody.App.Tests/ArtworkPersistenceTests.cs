using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Artwork;
using HeyMelodyWindows.Resources;

/// <summary>Remembered appearance, background artwork refresh, product Lottie playback and the settings cards.</summary>
internal static class ArtworkPersistenceTests
{
    public static int Run(HeadlessUnitTestSession session, string output, List<object> receipts)
    {
        int passed = 0;
        Run("remembered appearance shows artwork without a session and survives a cleared snapshot", RememberedAsync);
        Run("a new resource revision re-resolves in the background and keeps identical renderers", BackgroundRevisionAsync);
        Run("learning the color keeps the previous artwork until the colored package is ready", ColorLearnedAsync);
        Run("failed artwork requests retry with backoff instead of waiting for a refresh", RetryAsync);
        Run("a different product never borrows the previous product's artwork", ProductChangeAsync);
        Run("product Lottie plans hold, loop seamless timelines, follow markers and crop margins", LottiePlansAsync);
        Run("a disconnected X4 shows its remembered bundled artwork in the main window", () => DisconnectedWindowAsync(output, receipts));
        foreach (var dark in new[] { false, true })
            Run($"settings cards re-download with progress and results ({(dark ? "dark" : "light")})", () => SettingsCardsAsync(dark, output, receipts));
        Run("re-download is unavailable while online resources are off", OfflineSettingsAsync);
        return passed;

        void Run(string label, Func<Task> test)
        {
            session.Dispatch<bool>(async () => { await test(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
            passed++; Console.WriteLine("PASS " + label);
        }
    }

    private static async Task RememberedAsync()
    {
        var service = new ArtworkService();
        using var catalog = new DeviceVisualCatalog();
        var device = new UiDevice("earbuds", "耳机", false) { ProductId = "060410", Color = 0 };
        catalog.Update([device], device, null, null, "backend:1", service);
        Check(service.Requests.Count == 1 && service.Requests.All(r => r.ProductId == "060410" && r.Color == 0 && r.Role == "hero"),
            "Hints request main product artwork without fetching removed device icons.");
        service.CompleteAll(sha: "a");
        await PumpAsync(() => catalog.ProductFor(device) is FileBitmapVisual);
        var hero = catalog.ProductFor(device);
        catalog.Update([device with { ProductId = null, Color = null }], device, null, null, "backend:1", service);
        Check(ReferenceEquals(hero, catalog.ProductFor(device)) && service.Requests.Count == 1,
            "Losing the snapshot or hints never clears or re-requests remembered artwork.");
    }

    private static async Task BackgroundRevisionAsync()
    {
        var service = new ArtworkService();
        using var catalog = new DeviceVisualCatalog();
        var device = new UiDevice("earbuds", "耳机", true) { ProductId = "060410", Color = 0 };
        catalog.Update([device], device, "060410", 0, "backend:1", service);
        service.CompleteAll(sha: "same"); await PumpAsync(() => catalog.ProductFor(device) is FileBitmapVisual);
        var hero = catalog.ProductFor(device);
        catalog.Update([device], device, "060410", 0, "backend:2", service);
        Check(service.Requests.Count == 2 && ReferenceEquals(hero, catalog.ProductFor(device)), "A revision keeps the shown art while checking.");
        service.CompleteAll(sha: "same"); await PumpAsync(() => service.Releases.Count == 1);
        Check(ReferenceEquals(hero, catalog.ProductFor(device)), "Identical packages keep the attached renderer.");
        catalog.Update([device], device, "060410", 0, "backend:3", service);
        service.CompleteAll(sha: "changed"); await PumpAsync(() => !ReferenceEquals(hero, catalog.ProductFor(device)));
        Check(catalog.ProductFor(device) is FileBitmapVisual && service.Releases.Count >= 2, "A changed package replaces the art and releases the old lease.");
    }

    private static async Task ColorLearnedAsync()
    {
        var service = new ArtworkService();
        using var catalog = new DeviceVisualCatalog();
        var device = new UiDevice("earbuds", "耳机", true) { ProductId = "060410" };
        catalog.Update([device], device, null, null, "backend:1", service);
        service.CompleteAll(sha: "uncolored"); await PumpAsync(() => catalog.ProductFor(device) is FileBitmapVisual);
        var uncolored = catalog.ProductFor(device);
        catalog.Update([device], device, "060410", 2, "backend:1", service);
        Check(service.Requests.Count(r => r.Color == 2) == 1 && ReferenceEquals(uncolored, catalog.ProductFor(device)), "No generic flash while the colored package loads.");
        service.CompleteAll(sha: "colored"); await PumpAsync(() => !ReferenceEquals(uncolored, catalog.ProductFor(device)));
        await PumpAsync(() => service.Releases.Count == 1);
        catalog.Update([device], device, null, null, "backend:1", service);
        Check(!ReferenceEquals(uncolored, catalog.ProductFor(device)) && service.Requests.Count == 2, "A cleared color does not fall back to the uncolored art.");
    }

    private static async Task RetryAsync()
    {
        var service = new ArtworkService { FailNext = 2 };
        using var catalog = new DeviceVisualCatalog { RetryDelays = [TimeSpan.FromMilliseconds(20)] };
        var device = new UiDevice("earbuds", "耳机", false) { ProductId = "060410", Color = 0 };
        catalog.Update([device], device, null, null, "backend:1", service);
        await PumpAsync(() => service.Requests.Count(r => r.Completion.Task.IsCompleted) == 2 && service.Requests.Count == 3);
        service.CompleteAll(sha: "a");
        await PumpAsync(() => catalog.ProductFor(device) is FileBitmapVisual);
    }

    private static async Task ProductChangeAsync()
    {
        var service = new ArtworkService();
        using var catalog = new DeviceVisualCatalog();
        var device = new UiDevice("earbuds", "耳机", true) { ProductId = "060410", Color = 0 };
        catalog.Update([device], device, null, null, "backend:1", service);
        service.CompleteAll(sha: "a"); await PumpAsync(() => catalog.ProductFor(device) is FileBitmapVisual);
        catalog.Update([device], device, "068C10", 1, "backend:1", service);
        Check(Equals(catalog.ProductFor(device), DeviceVisuals.LoadingProduct),
            "Another product's art is removed at once and its own first lookup shows the loading state.");
        service.CompleteAll(sha: "b"); await PumpAsync(() => catalog.ProductFor(device) is FileBitmapVisual);
    }

    private static Task LottiePlansAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "heymelody-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using (var still = Load(folder, "still", Rectangle("{\"a\":0,\"k\":100}"), "", LottiePresentation.Product))
            {
                var plan = still.Plan;
                Check(plan.Loops && plan.IntroEnd == 0 && plan.LoopEnd == still.Animation.Duration.TotalSeconds, "A seamless timeline loops whole.");
                Check(Near(plan.Crop.Left, 98.4) && Near(plan.Crop.Top, 58.4) && Near(plan.Crop.Right, 141.6) && Near(plan.Crop.Bottom, 101.6),
                    $"Transparent margins are cropped: {plan.Crop}");
            }
            using (var fade = Load(folder, "fade", Rectangle("{\"a\":1,\"k\":[{\"t\":0,\"s\":[100],\"e\":[0],\"i\":{\"x\":[1],\"y\":[1]},\"o\":{\"x\":[0],\"y\":[0]}},{\"t\":59,\"s\":[0]}]}"), "", LottiePresentation.Product))
            {
                var plan = fade.Plan;
                Check(!plan.Loops && plan.TimeAt(0.5, out bool playing) == 0.5 && !playing, "A one-way timeline plays once.");
                Check(plan.TimeAt(30, out bool settled) == plan.Hold && settled && plan.Hold < fade.Animation.Duration.TotalSeconds, "Its last frame is held.");
            }
            using (var marked = Load(folder, "marked", Rectangle("{\"a\":0,\"k\":100}"), ",\"markers\":[{\"tm\":0,\"cm\":\"intro\",\"dr\":30},{\"tm\":30,\"cm\":\"loop\",\"dr\":30}]", LottiePresentation.Product))
            {
                var plan = marked.Plan;
                Check(plan.TimeAt(0.5, out _) == 0.5 && Near(plan.TimeAt(1.5, out _), 1.5, 1e-6) && Near(plan.TimeAt(2.5, out bool settled), 1.5, 1e-6) && !settled,
                    "Marker segments play the intro once and loop the last segment.");
            }
            using (var guide = Load(folder, "guide", Rectangle("{\"a\":0,\"k\":100}"), "", LottiePresentation.Loop))
                Check(guide.Plan.Loops && guide.Plan.Crop == new SkiaSharp.SKRect(0, 0, 200, 200) && guide.Plan.TimeAt(3.5, out _) == 0.5,
                    "Guide animations keep whole-canvas looping.");
        }
        finally { Directory.Delete(folder, true); }
        return Task.CompletedTask;

        static LottiePlayback Load(string folder, string name, string layer, string extra, LottiePresentation presentation)
        {
            string path = Path.Combine(folder, name + ".json");
            File.WriteAllText(path, "{\"v\":\"5.7.4\",\"fr\":30,\"ip\":0,\"op\":90,\"w\":200,\"h\":200,\"layers\":[" + layer + "]" + extra + "}");
            return LottiePlayback.Load(new ArtworkFile(new Uri(path), folder, ""), presentation);
        }
        static string Rectangle(string opacity) =>
            "{\"ddd\":0,\"ind\":1,\"ty\":4,\"ks\":{\"o\":" + opacity + ",\"r\":{\"a\":0,\"k\":0},\"p\":{\"a\":0,\"k\":[120,80,0]},\"a\":{\"a\":0,\"k\":[0,0,0]},\"s\":{\"a\":0,\"k\":[100,100,100]}},"
            + "\"shapes\":[{\"ty\":\"gr\",\"it\":[{\"ty\":\"rc\",\"d\":1,\"s\":{\"a\":0,\"k\":[40,40]},\"p\":{\"a\":0,\"k\":[0,0]},\"r\":{\"a\":0,\"k\":0}},"
            + "{\"ty\":\"fl\",\"c\":{\"a\":0,\"k\":[1,0,0,1]},\"o\":{\"a\":0,\"k\":100}},{\"ty\":\"tr\",\"p\":{\"a\":0,\"k\":[0,0]},\"a\":{\"a\":0,\"k\":[0,0]},\"s\":{\"a\":0,\"k\":[100,100]},\"r\":{\"a\":0,\"k\":0},\"o\":{\"a\":0,\"k\":100}}]}],"
            + "\"ip\":0,\"op\":90,\"st\":0,\"bm\":0}";
        static bool Near(double actual, double expected, double tolerance = 4) => Math.Abs(actual - expected) <= tolerance;
    }

    private static async Task DisconnectedWindowAsync(string output, List<object> receipts)
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var service = new RememberedService();
        await using var vm = new MainViewModel(service, new NoConfirm());
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 900, Content = view, WindowDecorations = WindowDecorations.None };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var hero = view.FindControl<ProductArtwork>("HeroArt")!;
            await PumpAsync(() => hero.GetVisualDescendants().OfType<Image>().Any(image => image.Source is Bitmap));
            Check(vm.ProductId is null && !vm.HasVerifiedSnapshot && service.Connects == 0
                && service.Requests.Contains(("06EC10", 0, "hero")) && service.Requests.All(request => request.Role == "hero"),
                "Remembered hints request main art without a control session or device-icon request.");
            Save(window, output, "remembered-disconnected-light-1280x900.png", receipts);
        }
        finally { window.Close(); }
    }

    private static async Task SettingsCardsAsync(bool dark, string output, List<object> receipts)
    {
        Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var service = new RedownloadService();
        await using var vm = new MainViewModel(service, new NoConfirm());
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 900, Content = view, WindowDecorations = WindowDecorations.None };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var operation = view.OpenSettingsDialogAsync(); await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            var dialog = window.GetVisualDescendants().OfType<FAContentDialog>().Single(d => Equals(d.Title, "应用设置"));
            var cards = dialog.GetVisualDescendants().OfType<FASettingsExpanderItem>()
                .Where(card => card.IsVisible && card.Parent is StackPanel { Name: "SettingsRoot" }).ToArray();
            int expectedCards = 8 + (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? 1 : 0) + (OperatingSystem.IsMacOS() ? 0 : 1);
            Check(cards.Length == expectedCards && cards.All(card => card.IconSource is FASymbolIconSource)
                && cards.Any(card => card.Name == "ModelFrameRateCard"),
                "Every visible top-level setting, including the 3D frame rate, is an icon card; "
                + $"expected={expectedCards}, actual={cards.Length}.");
            var button = dialog.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "重新下载在线资源");
            var ring = dialog.GetVisualDescendants().OfType<FAProgressRing>().Single();
            var result = dialog.GetVisualDescendants().OfType<FAInfoBar>().Single(bar => bar.Name == "RedownloadResult");
            Check(button.IsEnabled && !ring.IsVisible && !result.IsVisible, "Re-download starts idle.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Check(service.Redownloads == 1 && ring.IsVisible && !button.IsEnabled && vm.IsRedownloadingResources, "Progress shows while downloading.");
            service.Complete(new(service.Settings, true, 2, 0, "已重新下载在线资源，更新了 2 项美术资源。"));
            await PumpAsync(() => result.IsOpen);
            Check(result.Severity == FAInfoBarSeverity.Success && Equals(result.Message, "已重新下载在线资源，更新了 2 项美术资源。")
                && !ring.IsVisible && button.IsEnabled && vm.LogText.Contains("更新了 2 项", StringComparison.Ordinal), "The result is shown and logged.");
            Save(window, output, $"settings-cards-{(dark ? "dark" : "light")}-1280x900.png", receipts);
            dialog.Hide(); await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(service.Saves == 0, "Re-downloading does not save resource drafts.");
        }
        finally { await view.DismissSettingsDialogAsync(); window.Close(); }
    }

    private static async Task OfflineSettingsAsync()
    {
        var service = new RedownloadService(); service.Settings = service.Settings with { UseOnlineResources = false };
        await using var vm = new MainViewModel(service, new NoConfirm());
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 900, Content = view, WindowDecorations = WindowDecorations.None };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var operation = view.OpenSettingsDialogAsync(); await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            var dialog = window.GetVisualDescendants().OfType<FAContentDialog>().Single(d => Equals(d.Title, "应用设置"));
            var button = dialog.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "重新下载在线资源");
            var card = dialog.GetVisualDescendants().OfType<FASettingsExpanderItem>().Single(c => Equals(c.Content, "重新下载在线资源"));
            Check(!button.IsEnabled && card.Description?.ToString()!.Contains("使用在线资源", StringComparison.Ordinal) == true, "Offline settings explain why re-download is disabled.");
            dialog.Hide(); await operation.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { await view.DismissSettingsDialogAsync(); window.Close(); }
    }

    private static void Save(Window window, string output, string name, List<object> receipts)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing Skia pixels");
        var path = Path.Combine(output, name);
        using (var stream = new FileStream(path, FileMode.CreateNew)) frame.Save(stream, PngBitmapEncoderOptions.Default);
        receipts.Add(new { scenario = Path.GetFileNameWithoutExtension(name), file = name,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() });
    }

    private static async Task PumpAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 300; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Condition was not reached.");
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

    private sealed class ArtworkService : IArtworkService
    {
        public sealed record Request(string ProductId, int? Color, string Role, TaskCompletionSource<UiArtwork?> Completion);
        public List<Request> Requests { get; } = [];
        public List<string> Releases { get; } = [];
        public int FailNext { get; set; }
        public Task<UiArtwork?> AcquireArtworkAsync(string productId, int? color, string role, CancellationToken token)
        {
            var completion = new TaskCompletionSource<UiArtwork?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(new(productId, color, role, completion));
            if (FailNext > 0) { FailNext--; completion.SetException(new IOException("transient")); }
            return completion.Task;
        }
        public Task ReleaseArtworkAsync(string id, CancellationToken token) { Releases.Add(id); return Task.CompletedTask; }
        public void CompleteAll(string sha)
        {
            foreach (var request in Requests.Where(r => !r.Completion.Task.IsCompleted).ToArray())
                request.Completion.TrySetResult(new(Guid.NewGuid().ToString("N"), Path.GetFullPath("artwork-" + request.Role + ".png"),
                    "bitmap", Path.GetFullPath("."), sha, 1, 1));
        }
    }

    private sealed class RememberedService : IHeadsetService, IArtworkService
    {
        private readonly BundledResourceStore _bundle = new();
        public List<(string ProductId, int? Color, string Role)> Requests { get; } = [];
        public int Connects { get; private set; }
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<UiDevice>>([new UiDevice("remembered-x4", "OPPO Enco X4", false) { ProductId = "06EC10", Color = 0 }]);
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) { Connects++; throw new IOException("offline"); }
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => throw new IOException("offline");
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<UiArtwork?> AcquireArtworkAsync(string productId, int? color, string role, CancellationToken token)
        {
            Requests.Add((productId, color, role));
            var asset = _bundle.FindAsset(productId, color, "detail")
                ?? throw new InvalidOperationException("Bundled X4 art missing.");
            string path = _bundle.FullPath(asset);
            return Task.FromResult<UiArtwork?>(new(Guid.NewGuid().ToString("N"), path, asset.Kind, Path.GetDirectoryName(path)!, asset.Sha256, asset.Length, 1));
        }
        public Task ReleaseArtworkAsync(string leaseId, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class RedownloadService : IHeadsetService, IResourceSettingsService
    {
        private readonly PreviewService _inner = new();
        private TaskCompletionSource<UiResourceRedownloadResult>? _pending;
        public UiResourceSettings Settings { get; set; } = new(Status: "使用已下载的在线资源");
        public int Saves { get; private set; }
        public int Redownloads { get; private set; }
        public Task<UiResourceSettings> GetResourceSettingsAsync(CancellationToken token) => Task.FromResult(Settings);
        public Task<UiResourceSettings> SaveResourceSettingsAsync(UiResourceSettings settings, CancellationToken token) { Saves++; return Task.FromResult(Settings = settings); }
        public Task<UiResourceRedownloadResult> RedownloadResourcesAsync(CancellationToken token)
        { Redownloads++; _pending = new(TaskCreationOptions.RunContinuationsAsynchronously); return _pending.Task; }
        public void Complete(UiResourceRedownloadResult result) => _pending!.TrySetResult(result);
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => _inner.EnumerateAsync(token);
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => _inner.ConnectAsync(identity, token);
        public Task DisconnectAsync(CancellationToken token) => _inner.DisconnectAsync(token);
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => _inner.RefreshAsync(token);
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => _inner.ApplyAsync(action, token);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
