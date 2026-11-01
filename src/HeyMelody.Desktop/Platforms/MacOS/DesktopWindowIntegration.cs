using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using FluentAvalonia.UI.Controls;
using HeyMelody.Diagnostics;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Ui;

/// <summary>Native macOS menus and product-wide Quit, independent of the window's local close workflow.</summary>
internal static class DesktopWindowIntegration
{
    internal static void Attach(Window window, IHeadsetEventService? service)
    {
        if (!OperatingSystem.IsMacOS()) return;
        window.ExtendClientAreaToDecorationsHint = false;
        window.WindowDecorations = WindowDecorations.Full;
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        Application.Current.Name = "HeyMelody";
        var applicationMenu = new NativeMenu();
        var about = new NativeMenuItem("关于 HeyMelody");
        about.Click += async (_, _) => await new FAContentDialog { Title = "HeyMelody", Content = "OPPO、OnePlus、realme 耳机控制",
            CloseButtonText = "关闭" }.ShowAsync(window);
        applicationMenu.Add(about);
        NativeMenu.SetMenu(Application.Current, applicationMenu);
        var menu = new NativeMenu();
        var file = new NativeMenu();
        var close = new NativeMenuItem("关闭窗口") { Gesture = new KeyGesture(Key.W, KeyModifiers.Meta) };
        close.Click += (_, _) => window.Close(); file.Add(close);
        menu.Add(new NativeMenuItem("文件") { Menu = file });
        NativeMenu.SetMenu(window, menu);
        bool closed = false, requesting = false, stopping = false;
        void OnEvent(BackendEvent notification) { if (notification.Kind == "stopping") stopping = true; }
        if (service is not null) service.EventReceived += OnEvent;
        async void OnShutdown(object? sender, ShutdownRequestedEventArgs args)
        {
            if (closed || stopping) return;
            args.Cancel = true;
            if (requesting) return;
            requesting = true;
            try
            {
                if (service is null) { window.Close(); return; }
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await service.RequestApplicationExitAsync(budget.Token);
            }
            catch (Exception error)
            {
                DiagnosticLog.Warning("ui.lifetime", "quitRequestFailed", "Product exit could not be requested", error);
                if (!closed)
                    await new FAContentDialog { Title = "暂时无法退出", Content = "无法联系共享后台。请检查后台状态后重试。",
                        CloseButtonText = "继续使用" }.ShowAsync(window);
            }
            finally { requesting = false; }
        }
        desktop.ShutdownRequested += OnShutdown;
        window.Closed += (_, _) =>
        {
            closed = true; desktop.ShutdownRequested -= OnShutdown;
            if (service is not null) service.EventReceived -= OnEvent;
        };
    }
}
