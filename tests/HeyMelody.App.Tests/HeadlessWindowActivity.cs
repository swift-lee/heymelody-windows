using System.Reflection;
using Avalonia.Controls;
using Avalonia.Platform;

internal static class HeadlessWindowActivity
{
    internal static void SetActive(Window window, bool active)
    {
        GetCallback(window, active)();
        if (window.IsActive != active)
            throw new InvalidOperationException("The platform callback did not change window activity to " + active + ".");
    }

    internal static Action GetCallback(Window window, bool active)
    {
        string name = active ? "Activated" : "Deactivated";
        var property = typeof(IWindowBaseImpl).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The headless window activity callback is missing: " + name);
        var implementation = window.PlatformImpl
            ?? throw new InvalidOperationException("The headless window has no platform implementation.");
        if (property.GetValue(implementation) is not Action callback)
            throw new InvalidOperationException("The headless window activity callback is not installed: " + name);
        return callback;
    }
}
