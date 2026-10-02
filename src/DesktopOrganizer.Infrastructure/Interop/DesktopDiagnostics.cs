using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DesktopOrganizer.Core.Desktop;

namespace DesktopOrganizer.Infrastructure.Interop;

public sealed record SurfaceDiagnostic(string Hwnd, string Parent, string Root, string Caption,
    uint ProcessId, bool Visible, bool Child, bool Popup, bool AppWindow, DesktopArea Bounds);
public sealed record MouseTargetDiagnostic(int X, int Y, string Hwnd, string ClassName, string ExpectedListView);

/// <summary>Read-only native inspection for manual verification; no Explorer changes.</summary>
[SupportedOSPlatform("windows")]
public static class DesktopDiagnostics
{
    public static MouseTargetDiagnostic MouseTarget(DesktopHandles handles, int x, int y)
    {
        using var dpi = new DesktopDpiScope();
        var target = NativeMethods.WindowFromPoint(new(x, y));
        return new(x, y, $"0x{target:X}", NativeMethods.ClassOf(target), $"0x{handles.IconList:X}");
    }
    public static int FenceHitAt(DesktopHandles handles, int x, int y)
    {
        using var dpi = new DesktopDpiScope();
        nint frame = 0;
        NativeMethods.EnumChildWindows(handles.Host, (window, _) =>
        {
            if (NativeMethods.ClassOf(window) == "DesktopOrganizer.FenceSurface" && NativeMethods.ProcessOf(window) == Environment.ProcessId)
            { frame = window; return false; }
            return true;
        }, 0);
        if (frame == 0 || !MouseMessagePoint.TryPack(x, y, out var parameter)) throw new InvalidOperationException("No own Fence to inspect.");
        return (int)NativeMethods.SendMessage(frame, 0x84, 0, parameter); // WM_NCHITTEST is a query; no mouse input is generated.
    }
    public static IReadOnlyList<DesktopArea> MonitorAreas()
    {
        using var dpi = new DesktopDpiScope();
        var areas = new List<DesktopArea>();
        if (!NativeMethods.EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref NativeMethods.Rect bounds, nint parameter) =>
        {
            var info = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (NativeMethods.GetMonitorInfo(monitor, ref info)) areas.Add(new(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top));
            return true;
        }, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return areas;
    }
    public static string IconVisibility(DesktopHandles handles)
    {
        using var view = new ShellFolderView(handles);
        Marshal.ThrowExceptionForHR(view.View.GetCurrentFolderFlags(out var flags));
        return $"FolderFlags=0x{flags:X}; IconListVisible={NativeMethods.IsWindowVisible(handles.IconList)}; ex=0x{NativeMethods.GetWindowStyle(handles.IconList, -20):X}; styles=0x{NativeMethods.SendMessage(handles.IconList, 0x1037, 0, 0):X}; bg=0x{NativeMethods.SendMessage(handles.IconList, 0x1000, 0, 0):X}";
    }
    public static void ShowProbeBackgrounds(Win32DesktopHost host, bool visible)
    {
        NativeMethods.EnumChildWindows(host.Handles!.Host, (window, _) =>
        {
            if (NativeMethods.ClassOf(window) == "DesktopOrganizer.FenceBackground" && NativeMethods.ProcessOf(window) == Environment.ProcessId)
                NativeMethods.ShowWindow(window, visible ? 8 : 0);
            return true;
        }, 0);
    }
    public static IReadOnlyList<string> Layers(DesktopHandles handles)
    {
        using var dpi = new DesktopDpiScope();
        var result = new List<string>();
        for (var sibling = NativeMethods.GetWindow(handles.Host, 5); sibling != 0; sibling = NativeMethods.GetWindow(sibling, 2))
        {
            NativeMethods.GetWindowRect(sibling, out var rect);
            result.Add($"0x{sibling:X} {NativeMethods.ClassOf(sibling)}: style=0x{NativeMethods.GetWindowStyle(sibling, -16):X}; ex=0x{NativeMethods.GetWindowStyle(sibling, -20):X}; bounds={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}");
        }
        return result;
    }
    public static void RepaintProbe(Win32DesktopHost host)
    {
        NativeMethods.EnumChildWindows(host.Handles!.Host, (window, _) =>
        {
            if (NativeMethods.ClassOf(window) == "DesktopOrganizer.FenceBackground" && NativeMethods.ProcessOf(window) == Environment.ProcessId)
            { NativeMethods.InvalidateRect(window, 0, false); NativeMethods.UpdateWindow(window); }
            return true;
        }, 0);
    }
    public static void ProbeBackgroundUnderListView(Win32DesktopHost host)
    {
        var handles = host.Handles ?? throw new InvalidOperationException("No host");
        NativeMethods.EnumChildWindows(handles.Host, (window, _) =>
        {
            if (NativeMethods.ClassOf(window) == "DesktopOrganizer.FenceBackground" && NativeMethods.ProcessOf(window) == Environment.ProcessId &&
                NativeMethods.GetParent(window) == handles.Host)
                NativeMethods.SetWindowPos(window, handles.IconList, 0, 0, 0, 0, 0x13);
            return true;
        }, 0);
    }
    public static void ProbeBackgroundAboveFrame(Win32DesktopHost host)
    {
        NativeMethods.EnumChildWindows(host.Handles!.Host, (window, _) =>
        {
            if (NativeMethods.ClassOf(window) == "DesktopOrganizer.FenceBackground" && NativeMethods.ProcessOf(window) == Environment.ProcessId)
                NativeMethods.SetWindowPos(window, 0, 0, 0, 0, 0, 0x13);
            return true;
        }, 0);
    }
    public static string WindowAt(DesktopPoint point)
    {
        using var dpi = new DesktopDpiScope();
        var window = NativeMethods.WindowFromPoint(new((int)Math.Round(point.X), (int)Math.Round(point.Y)));
        return $"0x{window:X}: {NativeMethods.ClassOf(window)}; PID={NativeMethods.ProcessOf(window)}";
    }
    public static IReadOnlyList<SurfaceDiagnostic> Inspect(DesktopHandles handles)
    {
        using var dpi = new DesktopDpiScope();
        var result = new List<SurfaceDiagnostic>();
        NativeMethods.EnumChildWindows(handles.Host, (window, _) =>
        {
            if (NativeMethods.ClassOf(window) != "DesktopOrganizer.FenceSurface") return true;
            var caption = new StringBuilder(256);
            NativeMethods.GetWindowText(window, caption, caption.Capacity);
            NativeMethods.GetWindowRect(window, out var rect);
            var style = (uint)NativeMethods.GetWindowStyle(window, -16);
            var extended = (uint)NativeMethods.GetWindowStyle(window, -20);
            result.Add(new($"0x{window:X}", $"0x{NativeMethods.GetParent(window):X}",
                $"0x{NativeMethods.GetAncestor(window, 2):X}", caption.ToString(), NativeMethods.ProcessOf(window),
                NativeMethods.IsWindowVisible(window), (style & NativeMethods.Child) != 0,
                (style & 0x80000000) != 0, (extended & 0x40000) != 0,
                new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)));
            return true;
        }, 0);
        return result;
    }

    // Captures actual screen pixels, including layered windows. Foreground apps are not hidden.
    public static byte[] CaptureScreenArea(DesktopArea area)
    {
        if (area.Width <= 0 || area.Height <= 0 || (long)area.Width * area.Height > 16_777_216)
            throw new ArgumentException("Invalid capture bounds.");
        var previousDpi = NativeMethods.SetThreadDpiAwarenessContext(-4);
        var screen = NativeMethods.GetDC(0);
        var dc = NativeMethods.CreateCompatibleDC(screen);
        nint bitmap = 0, previousBitmap = 0;
        try
        {
            if (screen == 0 || dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var pixels = new byte[checked(area.Width * area.Height * 4)];
            var info = new NativeMethods.BitmapInfo { Size = 40, Width = area.Width, Height = -area.Height,
                Planes = 1, BitCount = 32, SizeImage = (uint)pixels.Length };
            bitmap = NativeMethods.CreateDIBSection(dc, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            previousBitmap = NativeMethods.SelectObject(dc, bitmap);
            if (previousBitmap == 0 || previousBitmap == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!NativeMethods.BitBlt(dc, 0, 0, area.Width, area.Height, screen, area.X, area.Y, 0x40CC0020))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            if (previousBitmap != 0 && previousBitmap != -1) NativeMethods.SelectObject(dc, previousBitmap);
            if (bitmap != 0) NativeMethods.DeleteObject(bitmap);
            if (dc != 0) NativeMethods.DeleteDC(dc);
            if (screen != 0) NativeMethods.ReleaseDC(0, screen);
            if (previousDpi != 0) NativeMethods.SetThreadDpiAwarenessContext(previousDpi);
        }
    }
}
