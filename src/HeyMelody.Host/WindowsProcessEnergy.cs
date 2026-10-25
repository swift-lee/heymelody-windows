using System.Diagnostics;
namespace HeyMelodyWindows.Host;

// Host policy is shared; only the Windows adapter applies EcoQoS or Windows priorities.
internal sealed class WindowsProcessEnergy : IEnergyApplier
{
    public void Apply(Process process, EnergyMode mode, Action<string, int> failed)
    {
#if WINDOWS
        new HeyMelodyWindows.Windows.WindowsProcessEnergy().Apply(process, mode == EnergyMode.Eco, failed);
#endif
    }
    internal static bool TryExemptCurrentThread()
    {
#if WINDOWS
        return HeyMelodyWindows.Windows.WindowsProcessEnergy.TryExemptCurrentThread();
#else
        return true;
#endif
    }
}
