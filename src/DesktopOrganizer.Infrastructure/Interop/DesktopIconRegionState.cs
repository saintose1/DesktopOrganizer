using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopOrganizer.Infrastructure.Interop;

/// <summary>Read-only diagnostics and recovery of legacy journals. Animation does not modify the Shell window region.</summary>
internal static class DesktopIconRegionState
{
    internal static byte[]? Capture(nint window)
    {
        var region = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        if (region == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (NativeMethods.GetWindowRgn(window, region) == 0) return null;
            var size = NativeMethods.GetRegionData(region, 0, null);
            if (size == 0 || size > 1_048_576) throw new InvalidOperationException("Unsupported desktop region size.");
            var data = new byte[size];
            if (NativeMethods.GetRegionData(region, size, data) != size) throw new Win32Exception(Marshal.GetLastWin32Error());
            return data;
        }
        finally { NativeMethods.DeleteObject(region); }
    }
    internal static void Restore(nint window, byte[]? original)
    {
        using var dpi = new DesktopDpiScope(NativeMethods.GetWindowDpiAwarenessContext(window));
        var region = original is null ? 0 : NativeMethods.ExtCreateRegion(0, (uint)original.Length, original);
        if (original is not null && region == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (NativeMethods.SetWindowRgn(window, region, false) == 0)
        {
            if (region != 0) NativeMethods.DeleteObject(region);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        // SetWindowRgn owns the transferred region.
    }
}
