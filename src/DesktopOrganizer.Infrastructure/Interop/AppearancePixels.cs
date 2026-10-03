using System.Globalization;

namespace DesktopOrganizer.Infrastructure.Interop;

internal static class AppearancePixels
{
    // ULW requires premultiplied BGRA, including zero RGB when alpha is zero.
    public static uint FromHex(string color, double opacity)
    {
        var rgb = uint.Parse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var alpha = (uint)Math.Round(opacity * 255 / 100);
        uint Channel(int shift) => (uint)Math.Round(((rgb >> shift) & 255) * alpha / 255.0);
        return (alpha << 24) | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }
}
