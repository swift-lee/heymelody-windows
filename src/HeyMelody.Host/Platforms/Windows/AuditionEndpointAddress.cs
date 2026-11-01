using System.Globalization;
using System.Text.RegularExpressions;

namespace HeyMelodyWindows.Host;

internal static partial class AuditionEndpointAddress
{
    [GeneratedRegex(@"(?:^|&)([0-9A-Fa-f]{12})(?:_C[0-9A-Fa-f]{8})?$", RegexOptions.CultureInvariant)]
    private static partial Regex AddressTail();
    internal static bool TryParse(string? id, out ulong address)
    {
        address = 0;
        if (id is null || !id.StartsWith(@"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_", StringComparison.OrdinalIgnoreCase)) return false;
        string[] segments = id.Split('\\');
        if (segments.Length != 3) return false;
        var match = AddressTail().Match(segments[2]);
        return match.Success && ulong.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address)
            && address is > 0 and <= 0xFFFFFFFFFFFF;
    }
}
