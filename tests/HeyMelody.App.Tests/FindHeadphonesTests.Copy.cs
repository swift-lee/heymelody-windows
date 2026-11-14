using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

internal static partial class FindHeadphonesTests
{
    private static async Task CopyFeedbackAsync()
    {
        var service = new LocationService();
        service.State = service.State with
        {
            Devices = [
                new("unknown-a", true, Location(22.30123456, 114.20123456), "recorded", ""),
                new("unknown-b", true, Location(23.1, 115.2), "recorded", "")
            ],
            Revision = 2
        };
        await using var vm = new MainViewModel(service, new NoConfirm());
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 900 };
        TaskCompletionSource? pendingA = null, pendingB = null, pendingClose = null;
        var writes = 0;
        string? copied = null;
        view.WriteClipboardOverride = text => { writes++; copied = text; return Task.CompletedTask; };
        try
        {
            window.Show();
            view.FindControl<FASettingsExpander>("FindHeadphonesPanel")!.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            var copy = view.FindControl<ToggleButton>("CopyLocationButton")!;
            Check(view.FindControl<TextBlock>("LocationActionStatus") is null,
                "Location details do not expose a transient status row.");
            Check(copy.IsChecked == false && Equals(copy.Content, "复制坐标"), "Copy starts as an unselected toggle.");

            ClickCopy(window, copy);
            Check(writes == 1 && copied == "geo:22.30123456,114.20123456" && copy.IsChecked == true
                && Equals(copy.Content, "已复制"),
                "Successful copying confirms on the toggle without adding a separate status row.");
            await Task.Delay(3000); Dispatcher.UIThread.RunJobs();
            Check(copy.IsChecked == true, "Copy feedback remains visible before five seconds.");
            ClickCopy(window, copy);
            Check(writes == 2 && copy.IsChecked == true, "Clicking the selected toggle copies again instead of clearing confirmation.");
            await Task.Delay(2500); Dispatcher.UIThread.RunJobs();
            Check(copy.IsChecked == true, "A repeated copy restarts feedback rather than using the previous expiry.");
            await Task.Delay(2700); Dispatcher.UIThread.RunJobs();
            Check(copy.IsChecked == false && Equals(copy.Content, "复制坐标"),
                "The confirmation returns to Copy coordinates after five seconds.");

            view.WriteClipboardOverride = _ => Task.FromException(new IOException("Expected clipboard failure."));
            ClickCopy(window, copy);
            Check(copy.IsChecked == false && Equals(copy.Content, "复制坐标"),
                "Clipboard failures do not leave a false copied state.");

            pendingA = new(TaskCreationOptions.RunContinuationsAsynchronously);
            view.WriteClipboardOverride = text => { writes++; copied = text; return pendingA.Task; };
            var copyingA = view.CopyLocationAsync();
            var writesBeforeDuplicate = writes;
            ClickCopy(window, copy);
            Check(writes == writesBeforeDuplicate && copy.IsChecked == false,
                "Repeated clicks while the clipboard write is pending do not submit another write or show success.");
            await vm.SelectDeviceAsync(vm.Devices.Single(device => device.Identity == "unknown-b"));
            Check(copy.IsChecked == false, "Switching devices clears copy feedback.");
            pendingB = new(TaskCreationOptions.RunContinuationsAsynchronously);
            view.WriteClipboardOverride = text => { writes++; copied = text; return pendingB.Task; };
            var copyingB = view.CopyLocationAsync();
            pendingA.SetResult(); await copyingA;
            writesBeforeDuplicate = writes;
            ClickCopy(window, copy);
            Check(writes == writesBeforeDuplicate && copy.IsChecked == false,
                "The retired device's completion cannot clear the new device's in-flight guard or mark it copied.");
            pendingB.SetResult(); await copyingB;
            Check(copy.IsChecked == true && copied == "geo:23.1,115.2",
                "The selected device owns its clipboard result and confirmation.");

            await view.DismissSettingsDialogAsync();
            Check(copy.IsChecked == false && Equals(copy.Content, "复制坐标"), "Closing the page cancels and resets the timer.");
            view.ResumeSettingsDialogs();
            pendingClose = new(TaskCreationOptions.RunContinuationsAsynchronously);
            view.WriteClipboardOverride = _ => pendingClose.Task;
            var copyingWhileClosing = view.CopyLocationAsync();
            await view.DismissSettingsDialogAsync();
            pendingClose.SetResult(); await copyingWhileClosing;
            Check(copy.IsChecked == false, "A clipboard result arriving after page closure cannot revive feedback.");
            view.ResumeSettingsDialogs();
            view.WriteClipboardOverride = _ => Task.CompletedTask;
            await view.CopyLocationAsync();
            Check(copy.IsChecked == true, "A resumed page can copy normally.");
            window.Close();
            Check(copy.IsChecked == false && Equals(copy.Content, "复制坐标"), "Detaching the closed window restores the toggle.");
        }
        finally
        {
            pendingA?.TrySetResult(); pendingB?.TrySetResult(); pendingClose?.TrySetResult();
            await view.DismissSettingsDialogAsync(); window.Close();
        }
        Check(service.HeadsetWrites == 0, "Copy feedback never writes headset settings.");
    }

    private static void ClickCopy(Window window, ToggleButton copy)
    {
        copy.BringIntoView(); Dispatcher.UIThread.RunJobs();
        Check(copy.Focus(), "The copy toggle accepts keyboard focus.");
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        Dispatcher.UIThread.RunJobs();
    }
}
