using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DesktopOrganizer.Core.Desktop;

namespace DesktopOrganizer.Infrastructure.Interop;

[SupportedOSPlatform("windows")]
public static class DesktopShell
{
    public static void PlacePopup(nint window, DesktopPoint anchor)
    {
        using var dpi = new DesktopDpiScope();
        if (!NativeMethods.GetWindowRect(window, out var rect)) return;
        var point = new NativeMethods.Point((int)Math.Clamp(anchor.X, int.MinValue, int.MaxValue),
            (int)Math.Clamp(anchor.Y, int.MinValue, int.MaxValue));
        var monitor = NativeMethods.MonitorFromPoint(point, 2);
        var info = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var x = Math.Clamp(point.X, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - width));
        var y = Math.Clamp(point.Y, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - height));
        NativeMethods.SetWindowPos(window, 0, x, y, 0, 0, 0x15); // NOSIZE | NOZORDER | NOACTIVATE
    }

    /// <summary>Supported Shell automation equivalent of Show Desktop. No private messages.</summary>
    public static void ShowDesktop()
    {
        var type = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new InvalidOperationException("Shell.Application is unavailable.");
        var shell = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Could not create Shell.Application.");
        try { type.InvokeMember("MinimizeAll", System.Reflection.BindingFlags.InvokeMethod, null, shell, null); }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
}
