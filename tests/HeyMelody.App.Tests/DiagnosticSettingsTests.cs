using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
