using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

internal static class AncFeatureTests
{
    // Deliberately differ from X2/X3's protocol indices: the editor must use the supplied keys.
    private const int OffKey = 0x0215, OnKey = 0x0216;
    private const string Title = "强化人声", Description = "弱化环境声的同时突出人声";
    private const string ListeningDescription = "减弱周围噪音基础上保留人声/广播声";

    internal static int Run(HeadlessUnitTestSession session, string output)
    {
        Run("ANC voice switch sends only its configured child keys for mouse and keyboard input", InputAsync);
        Run("ANC voice switch keeps only the last explicit target while a write is running", QueuedInputAsync);
        Run("ANC unknown child remains explicit and state backfills never submit a setter", UnknownAsync);
        Run("ANC fine editor follows the selected mode and safely falls back for older hosts", VisibilityAsync);
        Run("ANC descriptions update tooltips and automation without rebuilding for value changes", DescriptionsAsync);
        Run("ANC voice row fits narrow light and dark layouts with a local-only preview", () => PreviewAsync(output));
        return 6;

        void Run(string name, Func<Task> action)
        {
            session.Dispatch<bool>(async () => { await action(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    private static async Task InputAsync()
    {
        await using var fixture = await Fixture.CreateAsync(OffKey);
        Check(fixture.Row.IsVisible && !fixture.Combo.IsVisible && fixture.Toggle.IsChecked == false,
            "Confirmed standard transparency shows an unchecked feature switch instead of a dropdown.");
        await ClickAsync(fixture, fixture.Toggle);
        Check(fixture.Service.Actions is [{ Kind: "Anc", Value: OnKey }] && fixture.Toggle.IsChecked == true,
            "Mouse input sends the configured on child exactly once.");
        fixture.Toggle.Focus();
        fixture.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        fixture.Window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        await SettleAsync();
        Check(fixture.Service.Actions is [{ Kind: "Anc", Value: OnKey }, { Kind: "Anc", Value: OffKey }]
            && fixture.Toggle.IsChecked == false, "Keyboard input sends the configured off child exactly once.");
    }

    private static async Task QueuedInputAsync()
    {
        await using var fixture = await Fixture.CreateAsync(OffKey);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.ReleaseWrite = release;
        await ClickAsync(fixture, fixture.Toggle);
        await fixture.Service.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        foreach (var unused in new[] { 1, 2, 3 }) await ClickAsync(fixture, fixture.Toggle);
        Check(fixture.Service.Actions is [{ Kind: "Anc", Value: OnKey }] && fixture.Toggle.IsChecked == false,
            "Rapid clicks remain available but do not start overlapping writes.");
        release.TrySetResult();
        for (var i = 0; i < 20 && fixture.Service.Actions.Count < 2; i++) await SettleAsync();
        Check(fixture.Service.Actions is [{ Kind: "Anc", Value: OnKey }, { Kind: "Anc", Value: OffKey }]
            && fixture.Toggle.IsChecked == false, "Only the final queued voice target is submitted after the first receipt.");
    }

    private static async Task UnknownAsync()
    {
        await using var fixture = await Fixture.CreateAsync(null);
        Check(fixture.Toggle.IsChecked is null && fixture.State.IsEffectivelyVisible && fixture.Toggle.IsEnabled,
            "A known parent with an unknown child remains visibly unconfirmed and accepts an explicit choice.");
        fixture.Toggle.IsChecked = true; Dispatcher.UIThread.RunJobs();
        Check(fixture.Service.Actions.Count == 0, "Programmatic checkbox changes are not input commits.");
        foreach (var key in new int?[] { OnKey, OffKey, null })
        {
            await fixture.ObserveAsync(key);
            Check(fixture.Toggle.IsChecked == (key is null ? null : key == OnKey) && fixture.Service.Actions.Count == 0,
                "State backfill updates the switch without writing.");
        }
        await ClickAsync(fixture, fixture.Toggle);
        Check(fixture.Service.Actions.Count == 1 && fixture.Service.Actions[0].Kind == "Anc"
            && fixture.Service.Actions[0].Value == (fixture.Toggle.IsChecked == true ? OnKey : OffKey),
            "Clicking an unknown switch submits one explicit child rather than a guessed parent or default.");
        fixture.Service.Snapshot.CanSetAnc = false;
        await fixture.Model.RefreshStateCommand.ExecuteAsync(null); await SettleAsync();
        Check(!fixture.Toggle.IsEnabled, "The new switch respects the existing ANC write capability.");
    }

    private static async Task VisibilityAsync()
    {
        await using var fixture = await Fixture.CreateAsync(OnKey);
        await ClickAsync(fixture, fixture.Radio("降噪"));
        Check(!fixture.Row.IsVisible && fixture.Combo.IsVisible
            && fixture.Service.Actions is [{ Kind: "AncMode", Value: 1 }],
            "Leaving transparency restores ANC levels and submits only the requested parent.");
        await ClickAsync(fixture, fixture.Radio("通透"));
        Check(fixture.Row.IsVisible && !fixture.Combo.IsVisible && fixture.Toggle.IsChecked is null
            && fixture.Service.Actions is [{ Kind: "AncMode", Value: 1 }, { Kind: "AncMode", Value: 2 }],
            "Returning to transparency reveals an unknown feature value without replaying its previous setting.");
        fixture.Service.ReplaceTransparency(fixture.Service.Transparency with { Toggle = null });
        await fixture.ObserveAsync(OffKey);
        Check(!fixture.Row.IsVisible && fixture.Combo.IsVisible && fixture.Combo.SelectedItem is UiOption { Key: OffKey },
            "An older host without presentation metadata retains its explicit child dropdown.");
        Check(AutomationProperties.GetName(fixture.Combo) == "通透选项",
            "Legacy child editor automation names follow the selected main mode.");
        fixture.Service.ReplaceTransparency(fixture.Service.Transparency with
            { Toggle = new(OffKey, 0x02FF, Title, Description) });
        await fixture.ObserveAsync(OffKey);
        Check(!fixture.Row.IsVisible && fixture.Combo.IsVisible,
            "Incomplete toggle metadata cannot reinterpret an unavailable child as a supported switch.");
    }

    private static async Task DescriptionsAsync()
    {
        await using var fixture = await Fixture.CreateAsync(OnKey);
        var radio = fixture.Radio("聆听");
        Check(Equals(ToolTip.GetTip(radio), ListeningDescription)
            && AutomationProperties.GetHelpText(radio) == ListeningDescription,
            "The host's main-mode explanation is available in both tooltip and automation help.");
        var initial = fixture.Toggle;
        await fixture.ObserveAsync(OffKey);
        Check(ReferenceEquals(initial, fixture.Toggle), "Value-only refresh preserves the editor and keyboard focus.");
        fixture.Service.ReplaceTransparency(fixture.Service.Transparency with
            { Toggle = new(OffKey, OnKey, "更新的标题", "更新的说明"), Description = "新的主模式说明" });
        await fixture.ObserveAsync(OnKey);
        Check(!ReferenceEquals(initial, fixture.Toggle) && fixture.Title.Text == "更新的标题"
            && fixture.Description.Text == "更新的说明"
            && Equals(ToolTip.GetTip(fixture.Radio("通透")), "新的主模式说明"),
            "A metadata-only change refreshes the setting row and parent help without changing protocol keys.");
        var previousEditor = fixture.Toggle;
        const int newOffKey = 0x0221, newOnKey = 0x0222;
        fixture.Service.ReplaceTransparency(fixture.Service.Transparency with
        {
            Options = [new(newOffKey, "通透"), new(newOnKey, Title)],
            Toggle = new(newOffKey, newOnKey, Title, Description)
        });
        await fixture.ObserveAsync(newOffKey);
        previousEditor.RaiseEvent(new RoutedEventArgs(ToggleSwitch.ClickEvent)); await SettleAsync();
        Check(fixture.Service.Actions.Count == 0 && !ReferenceEquals(previousEditor, fixture.Toggle),
            "An editor replaced by new key metadata cannot submit its old or new child mapping.");
        await ClickAsync(fixture, fixture.Toggle);
        Check(fixture.Service.Actions is [{ Kind: "Anc", Value: newOnKey }],
            "The replacement editor submits its current configured child key.");
    }

    private static async Task PreviewAsync(string output)
    {
        var previousTheme = Application.Current!.RequestedThemeVariant;
        var bitmaps = new List<RenderTargetBitmap>();
        var gallery = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,Auto,Auto") };
        var columnWidths = new double[2];
        var rowHeights = new double[3];
        try
        {
            for (var column = 0; column < 2; column++)
            {
                bool dark = column == 1;
                Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                var states = new int?[] { null, OffKey, OnKey };
                for (var rowIndex = 0; rowIndex < states.Length; rowIndex++)
                {
                    await using var fixture = await Fixture.CreateAsync(states[rowIndex], width: 420);
                    CheckRowGeometry(fixture, (dark ? "dark" : "light") + "/" + (states[rowIndex]?.ToString("X4") ?? "unknown"));
                    Check(fixture.Row.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == Description),
                        "The official feature description remains present in the narrow setting row.");
                    Check(fixture.State.IsEffectivelyVisible == (states[rowIndex] is null),
                        "Unconfirmed status text is visible only for an unknown child.");
                    var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(fixture.Row.Bounds.Width),
                        (int)Math.Ceiling(fixture.Row.Bounds.Height)), new Vector(96, 96));
                    bitmap.Render(fixture.Row); bitmaps.Add(bitmap);
                    columnWidths[column] = Math.Max(columnWidths[column], bitmap.PixelSize.Width + 16);
                    rowHeights[rowIndex] = Math.Max(rowHeights[rowIndex], bitmap.PixelSize.Height + 48);
                    var label = states[rowIndex] is null ? "未确认" : states[rowIndex] == OnKey ? "开启" : "关闭";
                    var cell = new Border { Name = (dark ? "Dark" : "Light") + label,
                        MinHeight = bitmap.PixelSize.Height + 48,
                        Background = new SolidColorBrush(Color.Parse(dark ? "#202020" : "#F3F3F3")),
                        Padding = new Thickness(8), Child = new StackPanel { Spacing = 4, Children =
                        {
                            new TextBlock { Text = (dark ? "深色" : "浅色") + " · " + label,
                                Foreground = dark ? Brushes.White : Brushes.Black },
                            new Image { Source = bitmap, Stretch = Stretch.None, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left }
                        } } };
                    Grid.SetColumn(cell, column); Grid.SetRow(cell, rowIndex); gallery.Children.Add(cell);
                }
            }
            var canvasSize = new Size(Math.Ceiling(columnWidths.Sum()), Math.Ceiling(rowHeights.Sum()));
            var preview = new Window { Width = canvasSize.Width, Height = canvasSize.Height,
                Content = gallery, WindowDecorations = WindowDecorations.None };
            try
            {
                preview.Show(); await SettleAsync();
                foreach (var cell in gallery.Children.OfType<Border>())
                {
                    var position = cell.TranslatePoint(default, gallery)!.Value;
                    var image = cell.GetVisualDescendants().OfType<Image>().Single();
                    var imagePosition = image.TranslatePoint(default, gallery)!.Value;
                    Check(position.X >= 0 && position.Y >= 0
                        && position.X + cell.Bounds.Width <= canvasSize.Width + .5
                        && position.Y + cell.Bounds.Height <= canvasSize.Height + .5
                        && imagePosition.Y + image.Bounds.Height <= canvasSize.Height + .5,
                        "Every local preview cell and its complete row image fit inside the measured canvas.");
                    Console.WriteLine($"ANC preview {cell.Name}: cell={new Rect(position, cell.Bounds.Size)} image={new Rect(imagePosition, image.Bounds.Size)} canvas={canvasSize}");
                }
                using var frame = preview.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing ANC region pixels.");
                var path = Path.Combine(output, "anc-feature-region.png");
                using (var stream = File.Create(path)) frame.Save(stream, PngBitmapEncoderOptions.Default);
                Check(new FileInfo(path).Length < 150_000, "The local ANC preview remains below the image upload budget.");
            }
            finally { preview.Close(); }
        }
        finally
        {
            Application.Current.RequestedThemeVariant = previousTheme;
            foreach (var bitmap in bitmaps) bitmap.Dispose();
        }
    }

    private static void CheckRowGeometry(Fixture fixture, string state)
    {
        var row = new Rect(fixture.Row.Bounds.Size);
        var title = Bounds(fixture.Title);
        var description = Bounds(fixture.Description);
        var toggle = Bounds(fixture.Toggle);
        Check(title.Width > 0 && description.Width > 0 && toggle.Width > 0
            && Contains(title) && Contains(description) && Contains(toggle)
            && toggle.Left >= Math.Max(title.Right, description.Right) + 8
            && toggle.Center.Y >= title.Top && toggle.Center.Y <= description.Bottom,
            "The title and wrapping description remain on the left, with the complete switch on their right inside the setting row. "
            + $"State={state}; row={row}; title={title}; description={description}; toggle={toggle}.");
        Console.WriteLine($"ANC row {state}: size={row.Size} title={title} description={description} toggle={toggle}");
        return;

        Rect Bounds(Control control) => new(control.TranslatePoint(default, fixture.Row)!.Value, control.Bounds.Size);
        bool Contains(Rect bounds) => bounds.Left >= -.5 && bounds.Top >= -.5
            && bounds.Right <= row.Width + .5 && bounds.Bottom <= row.Height + .5;
    }

    private static async Task ClickAsync(Fixture fixture, Control control)
    {
        var point = await MainUiTestLayout.ClickPointAsync(fixture.Window, control);
        fixture.Window.MouseDown(point, MouseButton.Left); fixture.Window.MouseUp(point, MouseButton.Left);
        await SettleAsync();
    }

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); await Task.Delay(20);
        }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture : IAsyncDisposable
    {
        public Service Service { get; } = new();
        public MainViewModel Model { get; }
        public AncModeSelector Selector { get; }
        public Window Window { get; }
        public ToggleSwitch Toggle => Selector.GetVisualDescendants().OfType<ToggleSwitch>().Single();
        public FASettingsExpanderItem Row => Selector.GetVisualDescendants().OfType<FASettingsExpanderItem>().Single();
        public ComboBox Combo => Selector.GetVisualDescendants().OfType<ComboBox>().Single();
        public TextBlock State => Selector.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "AncFeatureState");
        public TextBlock Title => Selector.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "AncFeatureTitle");
        public TextBlock Description => Selector.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "AncFeatureDescription");
        private Fixture(double width)
        {
            Model = new(Service, new NoConfirm()); Selector = new() { DataContext = Model };
            Window = new() { Width = width, Height = 380, WindowDecorations = WindowDecorations.None,
                Content = new FASettingsExpander { Header = "降噪", Description = "调整你听见的环境声音", IsExpanded = true,
                    Items = { Selector } } };
        }
        public static async Task<Fixture> CreateAsync(int? key, double width = 560)
        {
            var fixture = new Fixture(width); fixture.Service.InitialKey = key;
            await fixture.Model.InitializeAsync(); await fixture.Model.ConnectCommand.ExecuteAsync(null);
            fixture.Window.Show(); await SettleAsync(); return fixture;
        }
        public RadioButton Radio(string name) => Selector.GetVisualDescendants().OfType<RadioButton>()
            .Single(radio => AutomationProperties.GetName(radio) == name);
        public async Task ObserveAsync(int? key)
        {
            Service.Snapshot.AncPrimary = 2; Service.Snapshot.Anc = key;
            await Model.RefreshStateCommand.ExecuteAsync(null); await SettleAsync();
        }
        public async ValueTask DisposeAsync() { Window.Close(); await Model.DisposeAsync(); }
    }

    private sealed class NoConfirm : IConfirmationService
    { public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) => Task.FromResult(false); }

    private sealed class Service : IHeadsetService
    {
        private readonly PreviewService _inner = new();
        public int? InitialKey { get; set; }
        public UiSnapshot Snapshot { get; private set; } = null!;
        public List<UiAction> Actions { get; } = [];
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? ReleaseWrite { get; set; }
        public UiAncMode Transparency => Snapshot.AncModes.Single(mode => mode.Key == 2);
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => _inner.EnumerateAsync(token);
        public async Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token)
        {
            Snapshot = await _inner.ConnectAsync(identity, token);
            Snapshot.AncModes =
            [
                new(1, "降噪", "anc", [new(0x0104, "深度降噪"), new(0x0106, "轻度降噪")]),
                new(2, "通透", "transparency", [new(OffKey, "通透"), new(OnKey, Title)])
                    { Toggle = new(OffKey, OnKey, Title, Description) },
                new(3, "聆听", "transparency", [new(0x0303, "聆听")]) { Description = ListeningDescription }
            ];
            Snapshot.AncOptions = Snapshot.AncModes.SelectMany(mode => mode.Options).ToArray();
            Snapshot.AncPrimary = 2; Snapshot.Anc = InitialKey;
            Snapshot.CanSetAnc = true; Snapshot.CanSetAncMode = true;
            return Snapshot;
        }
        public void ReplaceTransparency(UiAncMode mode)
        {
            Snapshot.AncModes = Snapshot.AncModes.Select(previous => previous.Key == 2 ? mode : previous).ToArray();
            Snapshot.AncOptions = Snapshot.AncModes.SelectMany(previous => previous.Options).ToArray();
        }
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => Task.FromResult(Snapshot);
        public Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken token) => Task.FromResult(Snapshot);
        public async Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        {
            Actions.Add(action);
            if (ReleaseWrite is { } release)
            {
                ReleaseWrite = null; WriteStarted.TrySetResult(); await release.Task.WaitAsync(token);
            }
            Snapshot.AncPrimary = action.Kind == "AncMode" ? action.Value : action.Value >> 8;
            Snapshot.Anc = action.Kind == "AncMode" ? null : action.Value;
            return new UiApplyResult { BackendReceiptReceived = true, Accepted = true,
                SubmissionAttempted = true, TargetSubmitted = true, ReadbackMatches = true,
                ConnectionState = ConnectionState.Connected, After = Snapshot };
        }
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
