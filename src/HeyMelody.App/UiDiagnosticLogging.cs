using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Logging;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Ui;

internal static class UiDiagnosticLogging
{
    private static int _dispatcherHooked;
    internal static AppBuilder UseHeyMelodyDiagnostics(this AppBuilder builder)
    {
        Logger.Sink = new FrameworkSink();
        if (Interlocked.Exchange(ref _dispatcherHooked, 1) == 0)
            Dispatcher.UIThread.UnhandledException += (_, args) =>
            {
                DiagnosticLog.Write(DiagnosticLevel.Critical, "Avalonia", "dispatcher.unhandled",
                    "Unhandled UI exception", exception: args.Exception);
                DiagnosticLog.Flush();
                // Keep Avalonia's original exception behavior.
            };
        return builder;
    }

    internal static void AttachDiagnosticNotice(Window window)
    {
        if (window.Content is not Avalonia.Controls.Control content) return;
        window.Content = null;
        var notice = new FAInfoBar
        {
            Title = "诊断日志不完整", Severity = FAInfoBarSeverity.Warning, IsClosable = false,
            IsOpen = false, IsVisible = false, Margin = new Thickness(8, 4)
        };
        var panel = new DockPanel();
        DockPanel.SetDock(notice, Dock.Top);
        panel.Children.Add(notice);
        panel.Children.Add(content);
        window.Content = panel;
        bool closed = false;
        var failures = new Dictionary<string, DiagnosticFailure>();
        void Present(DiagnosticFailure failure)
        {
            if (closed) return;
            failures[failure.ProcessInstanceId] = failure;
            notice.Message = string.Join(Environment.NewLine, failures.Values.Select(item => item.Component + ": " + item.Message));
            notice.IsVisible = notice.IsOpen = true;
        }
        void Changed(DiagnosticFailure failure) => Dispatcher.UIThread.Post(() => Present(failure));
        DiagnosticLog.Failed += Changed;
        foreach (var failure in DiagnosticLog.GetFailures()) Present(failure);
        window.Closed += (_, _) => { closed = true; DiagnosticLog.Failed -= Changed; };
    }

    private sealed class FrameworkSink : ILogSink
    {
        public bool IsEnabled(LogEventLevel level, string area) => level >= (DiagnosticLog.IsEnabled ? LogEventLevel.Debug : LogEventLevel.Warning);
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
            => Log(level, area, source, messageTemplate, []);
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (!IsEnabled(level, area)) return;
            var values = propertyValues.Select(value =>
            {
                try { return value?.ToString(); } catch { return value?.GetType().FullName; }
            }).ToArray();
            if (!DiagnosticLog.IsEnabled)
            {
                Trace.WriteLine($"[{level}] {area}: {messageTemplate} {string.Join(", ", values)}");
                return;
            }
            var mapped = level switch
            {
                LogEventLevel.Fatal => DiagnosticLevel.Critical, LogEventLevel.Error => DiagnosticLevel.Error,
                LogEventLevel.Warning => DiagnosticLevel.Warning, LogEventLevel.Information => DiagnosticLevel.Information,
                _ => DiagnosticLevel.Debug
            };
            DiagnosticLog.Write(mapped, "Avalonia." + area, "framework.log", messageTemplate,
                new { source = source?.GetType().FullName, values });
        }
    }
}
