using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelody.Diagnostics;
using HeyMelodyWindows.App;

internal static class DiagnosticSettingsTests
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "HeyMelody-diagnostic-settings-" + Guid.NewGuid().ToString("N"));
        var oldRoot = DiagnosticPaths.DataRootOverride;
        DiagnosticPaths.DataRootOverride = root;
        int passed = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            passed++; Console.WriteLine("PASS " + name);
        }
        using var session = HeadlessUnitTestSession.StartNew(typeof(PreviewBootstrap));
        try
        {
            session.Dispatch<bool>(() =>
            {
                var panel = new AppSettingsPanel();
                var window = new Window { Width = 600, Height = 850, Content = new ScrollViewer { Content = panel } };
                try
                {
                    window.Show();
                    var expander = panel.FindControl<FASettingsExpander>("DiagnosticExpander")!;
                    expander.IsExpanded = true;
                    Dispatcher.UIThread.RunJobs();
                    Check(panel.SelectedLogComponents.SequenceEqual(DiagnosticComponents.All), "five original component names selected by default");
                    var next = panel.FindControl<ToggleSwitch>("LogNextStartSwitch")!;
                    var open = panel.FindControl<Button>("OpenLogDirectoryButton")!;
                    Check(next.IsChecked != true && open.IsEnabled && AutomationProperties.GetName(open) == "打开日志目录",
                        "logging defaults off and directory button is always available with exact accessible label");
                    Check(!File.Exists(DiagnosticPaths.SettingsFile) && !DiagnosticLog.HasSession, "opening settings does not arm or create runtime session");
                    int opens = 0;
                    panel.OpenLogDirectoryOverride = path => { Check(path == DiagnosticPaths.LogRoot, "directory action targets fixed root"); opens++; };
                    open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(opens == 2 && Directory.Exists(DiagnosticPaths.LogRoot) && !File.Exists(DiagnosticPaths.SettingsFile),
                        "missing directory created independently of settings save and no backend/session started");
                    panel.OpenLogDirectoryOverride = _ => throw new IOException("fixture Explorer failed");
                    open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var notice = panel.FindControl<FAInfoBar>("LogSettingsNotice")!;
                    Check(notice.IsOpen && notice.IsVisible && notice.Message!.Contains("fixture Explorer failed"), "directory open failure visible without interrupting UI");
                    next.IsChecked = true;
                    Check(DiagnosticSettingsStore.Load().Pending is null, "toggle is a local draft until save");
                    Check(panel.TrySaveDiagnosticSettings(out _) && DiagnosticSettingsStore.Load().Pending is not null, "save atomically schedules selected components");
                    DiagnosticSettingsStore.Consume();
                    Check(panel.TrySaveDiagnosticSettings(out _) && DiagnosticSettingsStore.Load().Pending is null,
                        "ordinary save cannot restore consumed arm snapshot");
                    var changed = panel.FindControl<CheckBox>("LogFitCheck")!;
                    changed.IsChecked = false;
                    Check(panel.TrySaveDiagnosticSettings(out _) && DiagnosticSettingsStore.Load().Pending is null
                        && !DiagnosticSettingsStore.Load().SelectedComponents.Contains(DiagnosticComponents.FitTest),
                        "component draft persists without rearming consumed request");
                    next.IsChecked = false; next.IsChecked = true;
                    Check(panel.TrySaveDiagnosticSettings(out _) && DiagnosticSettingsStore.Load().Pending is not null, "explicit new toggle may schedule another cycle");
                    var cancelledPanel = new AppSettingsPanel();
                    cancelledPanel.FindControl<ToggleSwitch>("LogNextStartSwitch")!.IsChecked = false;
                    Check(DiagnosticSettingsStore.Load().Pending is not null, "cancelled dialog draft never changes saved request");
                    next.IsChecked = false;
                    Check(panel.TrySaveDiagnosticSettings(out _) && DiagnosticSettingsStore.Load().Pending is null, "saved off cancels only the next request");
                    foreach (var name in new[] { "LogMainCheck", "LogHostCheck", "LogManagerCheck", "LogFitCheck", "LogHearingCheck" })
                    {
                        var check = panel.FindControl<CheckBox>(name)!;
                        Check(DiagnosticComponents.All.Contains(AutomationProperties.GetName(check)), "component checkbox has untranslated accessible name");
                        check.IsChecked = false;
                    }
                    Check(!next.IsEnabled && next.IsChecked != true && open.IsEnabled, "empty selection disables scheduling but preserves directory action");
                    var hint = panel.FindControl<TextBlock>("LogNoComponentHint")!;
                    Check(hint.IsVisible, "empty selection explains why the switch is unavailable");
                    Check(ReferenceEquals(expander.Footer, next), "next-start switch is the section's primary header control");
                    var componentChecks = panel.FindControl<StackPanel>("LogComponentList")!.Children.OfType<CheckBox>().ToArray();
                    Check(componentChecks.Select(check => check.Content as string).SequenceEqual(DiagnosticComponents.All)
                        && componentChecks.All(check => AutomationProperties.GetName(check) == (string)check.Content!
                            && string.IsNullOrEmpty(AutomationProperties.GetHelpText(check)) && ToolTip.GetTip(check) is null)
                        && !expander.GetLogicalDescendants().OfType<FASettingsExpanderItem>().Any(item => !string.IsNullOrEmpty(item.Description)),
                        "components keep original names and none carries a private description");
                    var selectAll = panel.FindControl<CheckBox>("LogSelectAllCheck")!;
                    Check(selectAll.IsChecked == false && selectAll.IsThreeState == false, "select-all box mirrors an empty selection");
                    selectAll.IsChecked = true;
                    Check(panel.SelectedLogComponents.SequenceEqual(DiagnosticComponents.All) && next.IsEnabled && !hint.IsVisible,
                        "select all restores every component and re-enables scheduling");
                    panel.FindControl<CheckBox>("LogHostCheck")!.IsChecked = false;
                    Check(selectAll.IsChecked is null && next.IsEnabled, "partial selection shows the select-all box as mixed");
                    selectAll.IsChecked = false;
                    Check(panel.SelectedLogComponents.Length == 0 && selectAll.IsChecked == false && !next.IsEnabled && next.IsChecked != true,
                        "leaving the mixed state clears components and the next-start draft");
                    selectAll.IsChecked = true; next.IsChecked = true;
                    panel.FindControl<CheckBox>("LogMainCheck")!.IsChecked = false;
                    Check(selectAll.IsChecked is null && next.IsChecked == true, "unchecking one component keeps the draft while others remain");
                    selectAll.IsChecked = false;
                    Check(panel.SelectedLogComponents.Length == 0 && !next.IsEnabled && next.IsChecked != true, "select none clears components and the next-start draft");
                    Check(DiagnosticSettingsStore.Load().Pending is null, "bulk selection remains a local draft until save");

                    var status = panel.FindControl<FAInfoBar>("LogStatusBar")!;
                    var openSession = panel.FindControl<Button>("OpenSessionLogButton")!;
                    Check(!status.IsOpen && !status.IsVisible && !openSession.IsVisible, "idle Host shows no current-session status or action");
                    var directoryItem = panel.FindControl<FASettingsExpanderItem>("LogDirectoryItem")!;
                    Check(new Control[] { directoryItem, status, notice }.All(control => !control.GetLogicalAncestors().Contains(expander))
                        && directoryItem.Description == DiagnosticPaths.LogRoot,
                        "directory, current-cycle status and errors stay visible while the draft section is collapsed");
                    window.Width = 500; // the settings dialog's content width (548 px maximum minus padding)
                    window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                    var header = expander.GetVisualDescendants().OfType<FASettingsExpanderItem>().Single(item => item.Name == "ContentHost");
                    var switchMiddle = next.TranslatePoint(new Point(0, next.Bounds.Height / 2), header)!.Value.Y;
                    var onlineCard = panel.GetLogicalDescendants().OfType<FASettingsExpanderItem>().Single(item => Equals(item.Content, "使用在线资源"));
                    Check(header.Bounds.Width < 460 && switchMiddle > 0 && switchMiddle < header.Bounds.Height
                        && Math.Abs(header.Bounds.Height + 2 - onlineCard.Bounds.Height) < 1,
                        "at dialog width the switch stays beside the header, which is as tall as the other switch rows");
                    window.Width = 600; window.UpdateLayout();
                    var sessionDirectory = Path.Combine(DiagnosticPaths.LogRoot, "20261025-000000-fixture");
                    Directory.CreateDirectory(sessionDirectory);
                    var fixtureSession = new DiagnosticSessionInfo { Enabled = true, Directory = sessionDirectory,
                        Components = [DiagnosticComponents.Host], StartedAtUtc = DateTimeOffset.UtcNow };
                    panel.CurrentDiagnosticSessionOverride = () => fixtureSession;
                    expander.IsExpanded = false; expander.IsExpanded = true;
                    Dispatcher.UIThread.RunJobs();
                    Check(status.IsOpen && status.IsVisible && status.Title == "正在记录日志" && status.Message!.Contains(DiagnosticComponents.Host)
                        && openSession.IsVisible, "re-expanding refreshes the active Host cycle and offers its directory");
                    string? openedPath = null;
                    panel.OpenLogDirectoryOverride = path => openedPath = path;
                    openSession.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(openedPath == Path.GetFullPath(sessionDirectory) && !notice.IsOpen && !notice.IsVisible,
                        "current-session action opens that cycle and clears an earlier failure notice");
                    fixtureSession.Directory = Path.Combine(root, "outside-log-root");
                    Directory.CreateDirectory(fixtureSession.Directory);
                    openedPath = null;
                    openSession.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(openedPath is null && notice.IsOpen, "a descriptor pointing outside the log root is never opened");
                    Check(notice.IsClosable, "action failures can be dismissed");
                    notice.IsOpen = false;
                    Check(!notice.IsVisible, "dismissed notice leaves no gap");

                    Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticPaths.SettingsFile)!);
                    File.WriteAllText(DiagnosticPaths.SettingsFile, "{");
                    var failedPanel = new AppSettingsPanel();
                    var failedNotice = failedPanel.FindControl<FAInfoBar>("LogSettingsNotice")!;
                    Check(failedNotice.IsOpen && failedNotice.IsVisible && !failedNotice.IsClosable
                        && !failedPanel.FindControl<ToggleSwitch>("LogNextStartSwitch")!.IsEnabled
                        && !failedPanel.FindControl<CheckBox>("LogSelectAllCheck")!.IsEnabled
                        && failedPanel.FindControl<StackPanel>("LogComponentList")!.Children.OfType<CheckBox>().All(check => !check.IsEnabled)
                        && !failedPanel.FindControl<TextBlock>("LogNoComponentHint")!.IsVisible,
                        "unreadable settings disable the draft controls behind a notice that cannot be dismissed");
                    var failedOpen = failedPanel.FindControl<Button>("OpenLogDirectoryButton")!;
                    failedPanel.OpenLogDirectoryOverride = _ => { };
                    failedOpen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(failedOpen.IsEnabled && failedNotice.IsOpen, "directory action still works and keeps the load failure visible");
                    File.Delete(DiagnosticPaths.SettingsFile);
                }
                finally { window.Close(); }
                return Task.FromResult(true);
            }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"Diagnostic settings checks passed: {passed}; screenshots=0; Bluetooth=0; Explorer=0.");
            return 0;
        }
        finally { DiagnosticPaths.DataRootOverride = oldRoot; }
    }
}
