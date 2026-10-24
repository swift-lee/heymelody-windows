using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeyMelodyWindows.App;
using HeyMelodyWindows.DeviceManager;
using HeyMelodyWindows.Ui;

if (args is ["--native-exit-smoke"]) return WindowExitLifecycleTests.Run();
if (args is ["--native-rendering-policy-smoke", var renderMode, "--output-dir", var renderOutput]
    && renderMode is "default" or "basic")
    return NativeSmoke.Run(basicRenderMode: renderMode == "basic", outputDirectory: Path.GetFullPath(renderOutput));
if (args.FirstOrDefault() == "--native-rendering-policy-smoke")
{ Console.Error.WriteLine("Usage: --native-rendering-policy-smoke <default|basic> --output-dir path"); return 2; }
if (args is ["--native-smoke"]) return NativeSmoke.Run();
if (args is ["--native-alt-f4-smoke"]) return NativeSmoke.Run(closeWithAltF4: true);

int passed = 0;
using var session = HeadlessUnitTestSession.StartNew(typeof(TestBootstrap));
try
{
    Run("selection, refresh and exact save payload", SelectionAsync);
    Run("busy operations, save failure and explicit retry", SaveFailureAsync);
    Run("close cancels outstanding work without a successful result", ClosingAsync);
    Run("startup error can retry and corrupted configuration can be repaired", RecoveryAsync);
    Run("row pointer and keyboard selection, overlay geometry and theme layouts", LayoutAsync);
    Run("Bluetooth launcher does not enumerate or save devices", LauncherAsync);
    Run("backend catalog pushes preserve selected checkboxes and save failure feedback", CatalogPushAsync);
    Run("backend exit waits for the pending save and cancellation restores controls", BackendExitAsync);
    Console.WriteLine($"PASS {passed} device manager cases; nativeBluetoothCalls=0; whitelistWrites=0");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

void Run(string name, Func<Task> test)
{
    session.Dispatch<bool>(async () => { await test(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
    passed++; Console.WriteLine("PASS " + name);
}

static async Task SelectionAsync()
{
    var service = new FakeService { Catalog = new([new("a", "离线耳机", false, true), new("b", "在线耳机", true, false)]) };
    await using var vm = new DeviceManagerViewModel(service);
    Check(!vm.CanCommit && !vm.CancelCommand.CanExecute(null), "Zero selections disable both footer actions.");
    await vm.RefreshAsync();
    Check(vm.SelectedCount == 1 && vm.Devices[0].IsChecked && vm.Devices[0].Status == "已配对，未连接",
        "Persisted selection includes an offline paired device.");
    vm.Devices[0].IsChecked = false; vm.Devices[1].IsChecked = true;
    var kept = vm.Devices[1];
    service.Catalog = new([new("b", "已重命名耳机", false, false), new("c", "新设备", true, false)]);
    await vm.RefreshAsync();
    Check(vm.Devices[0].IsChecked && !vm.Devices[1].IsChecked && vm.Devices[0].Name == "已重命名耳机",
        "Refresh preserves unsaved selection by identity and updates names.");
    Check(ReferenceEquals(vm.Devices[0], kept) && vm.Devices[0].Status == "已配对，未连接" && vm.Devices.Count == 2,
        "A refresh updates the remaining device's row in place, so its control and keyboard focus survive.");
    var exits = 0; vm.ExitRequested += () => exits++;
    await vm.SaveAsync();
    Check(service.Saves == 1 && service.LastSaved!.SequenceEqual(["b"]) && vm.ExitCode == 0 && exits == 1,
        "Save sends only selected identities and exits only after success.");
}

static async Task SaveFailureAsync()
{
    var release = new TaskCompletionSource<UiWhitelistSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    var service = new FakeService { Save = (_, token) => release.Task.WaitAsync(token) };
    await using var vm = new DeviceManagerViewModel(service);
    await vm.RefreshAsync();
    var saving = vm.SaveAsync();
    Check(vm.Busy && !vm.CanCommit && !vm.CanRefresh && !vm.CancelCommand.CanExecute(null), "Pending save disables all mutation controls.");
    await vm.SaveAsync(); await vm.RefreshAsync();
    Check(service.Saves == 1 && service.Enumerations == 1, "Busy state prevents duplicated save and enumeration.");
    release.SetResult(new(false, "设备已经取消配对，请刷新。")); await saving;
    Check(vm.CanCommit && vm.Error == "设备已经取消配对，请刷新。" && vm.ExitCode == 1,
        "Failure keeps the window and selected draft available for retry.");
    service.Save = (_, _) => Task.FromResult(new UiWhitelistSaveResult(true));
    await vm.SaveAsync(); Check(vm.ExitCode == 0 && service.Saves == 2, "An explicit retry can save successfully.");
}

static async Task ClosingAsync()
{
    var release = new TaskCompletionSource<UiWhitelistSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    var service = new FakeService { Save = (_, token) => release.Task.WaitAsync(token) };
    var vm = new DeviceManagerViewModel(service);
    await vm.RefreshAsync(); var saving = vm.SaveAsync();
    var exits = 0; vm.ExitRequested += () => exits++;
    await vm.DisposeAsync(); await saving;
    release.TrySetResult(new(true));
    Check(vm.Closing && vm.ExitCode == 1 && exits == 0 && service.Disposals == 1,
        "Closing cancels pending work, does not report a successful save, and disposes the bridge once.");
    await vm.DisposeAsync(); Check(service.Disposals == 1, "Disposal is idempotent.");
}

static async Task RecoveryAsync()
{
    var service = new FakeService { ReadFailure = true };
    await using var vm = new DeviceManagerViewModel(service);
    await vm.RefreshAsync(); Check(vm.HasError && vm.ExitCode == 1 && !vm.CanCommit,
        "An initial read failure remains retryable and closing still returns cancellation.");
    service.ReadFailure = false;
    service.Catalog = new([new("a", "已配对耳机", false, false)], "设备选择记录已损坏，请重新选择并保存。");
    await vm.RefreshAsync();
    Check(vm.HasError && vm.Devices.Count == 1 && vm.SelectedCount == 0 && vm.ExitCode == 1 && service.Saves == 0,
        "Corrupted storage exposes candidates and a warning without an implicit write.");
    vm.Devices[0].IsChecked = true; await vm.SaveAsync(); Check(vm.ExitCode == 0, "Selecting and saving explicitly repairs the configuration.");
    var empty = new FakeService { Catalog = new([]) };
    await using var emptyVm = new DeviceManagerViewModel(empty); await emptyVm.RefreshAsync();
    Check(emptyVm.ShowEmpty && !emptyVm.CanCommit && emptyVm.ExitCode == 1, "An empty successful catalog has a clear empty state and disabled footer.");
}

static async Task LayoutAsync()
{
    var service = new FakeService { Catalog = new(Enumerable.Range(0, 12)
        .Select(index => new UiDeviceCandidate("row-" + index,
            index == 0 ? string.Concat(Enumerable.Repeat("名称很长的耳机", 12)) : "耳机 " + index,
            index % 2 == 0, index == 0)).ToArray()) };
    await using var vm = new DeviceManagerViewModel(service); await vm.RefreshAsync();
    var view = new DeviceManagerView { DataContext = vm };
    var window = new Window { Width = 520, Height = 720, WindowDecorations = WindowDecorations.None, Content = view };
    try
    {
        window.Show(); Dispatcher.UIThread.RunJobs();
        var rows = view.FindControl<ItemsControl>("DeviceList")!.GetVisualDescendants().OfType<CheckBox>().ToArray();
        Check(rows.Length == 12 && rows.All(row => row.Bounds.Height == 72), "Every device uses a 72 pixel row.");
        var row = rows[1];
        var point = row.TranslatePoint(new Point(30, 36), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Check(vm.Devices[1].IsChecked && vm.SelectedCount == 2, "Clicking the icon area toggles the entire row.");
        row.Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " "); Dispatcher.UIThread.RunJobs();
        Check(!vm.Devices[1].IsChecked && vm.SelectedCount == 1, "Keyboard Space toggles a focused row.");
        foreach (var dark in new[] { false, true })
        foreach (var size in new[] { new Size(520, 720), new Size(400, 480) })
        {
            Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            window.Width = size.Width; window.Height = size.Height; Dispatcher.UIThread.RunJobs();
            var overlay = view.FindControl<Border>("ActionOverlay")!;
            var origin = overlay.TranslatePoint(default, view)!.Value;
            Check(overlay.Bounds.Height == 72 && Math.Abs(origin.Y + 72 - view.Bounds.Height) <= 1,
                "The overlay remains fixed at the bottom in both sizes and themes.");
            var scroll = view.FindControl<ScrollViewer>("DeviceScroll")!;
            scroll.Offset = new Vector(0, scroll.Extent.Height); Dispatcher.UIThread.RunJobs();
            var lastBottom = rows[^1].TranslatePoint(new Point(0, rows[^1].Bounds.Height), view)!.Value.Y;
            Check(lastBottom <= origin.Y, "Internal bottom padding lets the final row scroll fully above the overlay.");
            foreach (var name in new[] { "SaveButton", "CancelButton" })
            {
                var button = view.FindControl<Button>(name)!; var buttonOrigin = button.TranslatePoint(default, view)!.Value;
                Check(buttonOrigin.X >= 0 && buttonOrigin.X + button.Bounds.Width <= view.Bounds.Width,
                    "Both footer actions remain within the minimum window width.");
            }
            scroll.Offset = default; Dispatcher.UIThread.RunJobs();
            var output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "device-manager-ui");
            Directory.CreateDirectory(output);
            window.CaptureRenderedFrame()?.Save(Path.Combine(output, $"{(dark ? "dark" : "light")}-{size.Width}x{size.Height}.png"), PngBitmapEncoderOptions.Default);
        }
    }
    finally { window.Close(); }
}

static async Task LauncherAsync()
{
    var service = new FakeService(); await using var vm = new DeviceManagerViewModel(service); await vm.RefreshAsync();
    var launcher = new FakeLauncher();
    var view = new DeviceManagerView { DataContext = vm, LauncherOverride = launcher };
    var window = new Window { Width = 520, Height = 720, WindowDecorations = WindowDecorations.None, Content = view };
    try
    {
        window.Show(); Dispatcher.UIThread.RunJobs();
        await view.OpenBluetoothSettingsAsync();
        Check(service.Enumerations == 1 && service.Saves == 0, "Opening settings never enumerates devices or persists a selection.");
        if (OperatingSystem.IsWindows())
            Check(launcher.Uris is [{ OriginalString: "ms-settings:bluetooth" }] && vm.HasError,
                "A launcher rejection gives an actionable error for the documented settings URI.");
        else Check(launcher.Uris.Count == 0, "Non-Windows preview cannot launch a Windows URI.");
    }
    finally { window.Close(); }
}

static async Task CatalogPushAsync()
{
    var service = new FakeService { Catalog = new([new("a", "第一副耳机", false, true), new("b", "第二副耳机", false, false)]) };
    await using var vm = new DeviceManagerViewModel(service); await vm.RefreshAsync();
    vm.Devices[0].IsChecked = false; vm.Devices[1].IsChecked = true;
    vm.AdoptBackendEvent(new() { Kind = "catalog", Catalog = new([new("a", "第一副耳机", true, true), new("b", "已重命名耳机", true, false)]) });
    Check(!vm.Devices[0].IsChecked && vm.Devices[1].IsChecked && vm.Devices.All(row => row.Connected)
        && service.Enumerations == 1 && service.Saves == 0, "A catalog push changes names and connection state without overwriting unsaved checks or querying again.");
    var release = new TaskCompletionSource<UiWhitelistSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    service.Save = (_, token) => release.Task.WaitAsync(token); var saving = vm.SaveAsync();
    vm.AdoptBackendEvent(new() { Kind = "catalog", Catalog = new([new("b", "已重命名耳机", false, false)]) });
    Check(vm.Devices.Count == 2 && vm.Busy, "A catalog arriving during a save waits for the frozen payload operation.");
    release.SetResult(new(false, "保存结果未确认，请重新选择。")); await saving;
    Check(vm.Devices.Count == 1 && vm.Devices[0].IsChecked && !vm.Devices[0].Connected && vm.HasError,
        "A deferred catalog updates rows after failure and keeps the retry explanation.");
    Check(service.LastSaved!.SequenceEqual(["b"]) && vm.ExitCode == 1, "Connection pushes do not alter the save target or report success.");
    vm.AdoptBackendEvent(new() { Kind = "unavailable", Message = "后台版本不兼容，请更新。" });
    Check(vm.Error.Contains("版本不兼容"), "Backend diagnostics retain an actionable version error.");
}

static async Task BackendExitAsync()
{
    var release = new TaskCompletionSource<UiWhitelistSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    var service = new FakeService { Save = (_, token) => release.Task.WaitAsync(token) };
    await using var vm = new DeviceManagerViewModel(service); await vm.RefreshAsync();
    var saving = vm.SaveAsync(); var vote = vm.PrepareBackendExitAsync();
    Check(!vote.IsCompleted && !vm.CanCommit && !vm.CanRefresh, "The tray exit vote waits for an in-flight selection save.");
    release.SetResult(new(false, "尚未保存。")); await saving; await vote;
    Check(!vm.CanCommit && !vm.CanRefresh && !vm.Closing && service.Disposals == 0,
        "An approved exit remains suspended until the backend's stopping phase.");
    vm.CancelBackendExit(); Check(vm.CanCommit && vm.CanRefresh && vm.ExitCode == 1,
        "A cancelled backend exit restores the unsaved selection controls.");
}

static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

public static class TestBootstrap
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<DeviceManagerApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseHeyMelodyRendering().UseHeyMelodyTypography();
}

internal sealed class FakeService : IDeviceManagementService, IAsyncDisposable
{
    public UiDeviceCatalog Catalog { get; set; } = new([new("device-a", "测试耳机", false, true)]);
    public bool ReadFailure { get; set; }
    public int Enumerations { get; private set; }
    public int Saves { get; private set; }
    public int Disposals { get; private set; }
    public IReadOnlyList<string>? LastSaved { get; private set; }
    public Func<IReadOnlyList<string>, CancellationToken, Task<UiWhitelistSaveResult>> Save { get; set; } = (_, _) => Task.FromResult(new UiWhitelistSaveResult(true));
    public Task<UiDeviceCatalog> EnumerateCandidatesAsync(CancellationToken token)
    { Enumerations++; return ReadFailure ? Task.FromException<UiDeviceCatalog>(new IOException()) : Task.FromResult(Catalog); }
    public Task<UiWhitelistSaveResult> SaveWhitelistAsync(IReadOnlyList<string> identities, CancellationToken token)
    { Saves++; LastSaved = identities; return Save(identities, token); }
    public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
}

internal sealed class FakeLauncher : ILauncher
{
    public List<Uri> Uris { get; } = [];
    public Task<bool> LaunchUriAsync(Uri uri) { Uris.Add(uri); return Task.FromResult(false); }
    public Task<bool> LaunchFileAsync(IStorageItem file) => throw new InvalidOperationException();
}
