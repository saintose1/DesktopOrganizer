using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DesktopOrganizer.Core.Desktop;

namespace DesktopOrganizer.Infrastructure.Interop;

public sealed record SurfaceDiagnostic(string Hwnd, string Parent, string Root, string Caption,
    uint ProcessId, bool Visible, bool Child, bool Popup, bool AppWindow, DesktopArea Bounds);
public sealed record MouseTargetDiagnostic(int X, int Y, string Hwnd, string ClassName, string ExpectedListView);
public sealed record DesktopInputDiagnostic(int X, int Y, string Capture, int SelectedIcons, bool LeftButtonDown);

/// <summary>Read-only native inspection for manual verification; no Explorer changes.</summary>
[SupportedOSPlatform("windows")]
public static class DesktopDiagnostics
{
    /// <summary>Fingerprint of the real Shell window region; animations must leave it unchanged.</summary>
    public static string IconRegion(DesktopHandles handles)
    {
        if (!new DesktopDiscovery(Microsoft.Extensions.Logging.Abstractions.NullLogger<DesktopDiscovery>.Instance).Validate(handles))
            throw new InvalidOperationException("Desktop host validation failed.");
        var bytes = DesktopIconRegionState.Capture(handles.IconList);
        return bytes is null ? "none" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }
    /// <summary>Native tray inspection/callback replay, not a physical notification-area click.</summary>
    public static string TrayState(uint processId, bool open = false, bool exit = false, bool menu = false)
    {
        nint owner = 0, main = 0;
        NativeMethods.EnumProc callback = (window, _) =>
        {
            if (NativeMethods.ProcessOf(window) != processId) return true;
            if (NativeMethods.ClassOf(window).StartsWith("DesktopOrganizer.Tray.", StringComparison.Ordinal)) owner = window;
            var caption = new StringBuilder(256); NativeMethods.GetWindowText(window, caption, caption.Capacity);
            if (caption.ToString() == "DesktopOrganizer — группы") main = window;
            return true;
        };
        NativeMethods.EnumWindows(callback, 0);
        if (owner == 0 || !NativeMethods.IsWindow(owner)) throw new InvalidOperationException("No validated DesktopOrganizer tray owner.");
        var identity = new TrayNative.IconIdentifier { Size = (uint)Marshal.SizeOf<TrayNative.IconIdentifier>(), Window = owner, Id = 1 };
        var found = TrayNative.GetIconRect(ref identity, out var rectangle) >= 0;
        if (open || exit || menu)
        {
            if (!NativeMethods.PostMessage(owner, exit ? 0x111 : WindowsTrayIcon.Callback,
                exit ? 2 : 0, exit ? 0 : menu ? 0x7b : 0x400)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return $"TrayHWND=0x{owner:X}; registered={found}; rect={rectangle.Left},{rectangle.Top},{rectangle.Right},{rectangle.Bottom}; " +
            $"MainHWND=0x{main:X}; visible={NativeMethods.IsWindowVisible(main)}; ownerVisible={NativeMethods.IsWindowVisible(owner)}";
    }
    /// <summary>Reproduces a native ListView raise for presentation checks, without moving icons or sending mouse input.</summary>
    public static void ProbeListViewRaise(Win32DesktopHost host)
    {
        var handles = host.Handles ?? throw new InvalidOperationException("No desktop host.");
        if (!new DesktopDiscovery(Microsoft.Extensions.Logging.Abstractions.NullLogger<DesktopDiscovery>.Instance).Validate(handles))
            throw new InvalidOperationException("Desktop host validation failed.");
        var repaint = host.RepaintCount; var bitmap = host.BitmapPresentCount;
        if (!NativeMethods.SetWindowPos(handles.IconList, 0, 0, 0, 0, 0, 0x1B))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        host.EnsureStacking();
        var seen = false;
        for (var window = NativeMethods.GetWindow(handles.Host, 5); window != 0; window = NativeMethods.GetWindow(window, 2))
        {
            if (window == handles.IconList) seen = true;
            if (NativeMethods.ProcessOf(window) != Environment.ProcessId) continue;
            var name = NativeMethods.ClassOf(window);
            if ((name == "DesktopOrganizer.FenceSurface" && seen) || (name == "DesktopOrganizer.FenceBackground" && !seen))
                throw new InvalidOperationException("Fence/ListView composition order was not restored.");
        }
        if (host.RepaintCount != repaint || host.BitmapPresentCount != bitmap)
            throw new InvalidOperationException("Z-order correction unexpectedly repainted/rasterized Fence content.");
    }
    public static DesktopInputDiagnostic InputState(DesktopHandles handles)
    {
        if (!new DesktopDiscovery(Microsoft.Extensions.Logging.Abstractions.NullLogger<DesktopDiscovery>.Instance).Validate(handles))
            throw new InvalidOperationException("No validated desktop input target.");
        using var dpi = new DesktopDpiScope();
        if (!NativeMethods.GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var thread = NativeMethods.GetWindowThreadProcessId(handles.IconList, out _);
        var info = new NativeMethods.GuiThreadInfo { Size = (uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        if (!NativeMethods.GetGUIThreadInfo(thread, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(point.X, point.Y, $"0x{info.Capture:X}",
            (int)NativeMethods.SendMessage(handles.IconList, 0x1032, 0, 0), // LVM_GETSELECTEDCOUNT, read only.
            (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0);
    }
    /// <summary>Checks only our hook/message-loop lifetime. Null HWNDs force every event to pass through.</summary>
    public static bool CheckBackgroundMouseRoutingLifetime()
    {
        using var route = new ExplorerBackgroundMouse(new(0, 0, 0, 0, 0, 0),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var ready = route.IsAvailable;
        route.Dispose();
        return ready && route.IsStopped;
    }
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
            if (NativeMethods.ClassOf(window) == "DesktopOrganizer.FenceSurface" && NativeMethods.ProcessOf(window) == Environment.ProcessId &&
                NativeMethods.GetWindowRect(window, out var bounds) && x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom)
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
    public static string CompositionState(DesktopHandles handles)
    {
        var attributes = NativeMethods.GetLayeredWindowAttributes(handles.IconList, out var key, out var alpha, out var flags)
            ? $"key=0x{key:X}; alpha={alpha}; flags=0x{flags:X}" : "none";
        return $"Visible={NativeMethods.IsWindowVisible(handles.IconList)}; ex=0x{NativeMethods.GetWindowStyle(handles.IconList, -20):X}; styles=0x{NativeMethods.SendMessage(handles.IconList, 0x1037, 0, 0):X}; bg=0x{NativeMethods.SendMessage(handles.IconList, 0x1000, 0, 0):X}; textBg=0x{NativeMethods.SendMessage(handles.IconList, 0x1025, 0, 0):X}; layeredAttributes={attributes}";
    }
    /// <summary>Actual lease/property transitions only, without synthesizing any Explorer mouse/OLE gesture.</summary>
    public static IReadOnlyList<string> CheckMarqueeCompositionLease(bool outline = true)
    {
        var discovery = new DesktopDiscovery(Microsoft.Extensions.Logging.Abstractions.NullLogger<DesktopDiscovery>.Instance);
        var handles = discovery.Find() ?? throw new InvalidOperationException("No Explorer desktop.");
        if (Inspect(handles).Count != 0) throw new InvalidOperationException("Close DesktopOrganizer before the reversible composition probe.");
        var before = CompositionState(handles);
        var result = new List<string> { "App off: " + before };
        var trace = new ExplorerInteractionTrace(Microsoft.Extensions.Logging.Abstractions.NullLogger<ExplorerInteractionTrace>.Instance);
        using (var host = new Win32DesktopHost(discovery, Microsoft.Extensions.Logging.Abstractions.NullLogger<Win32DesktopHost>.Instance, interactionTrace: trace))
        {
            if (!host.Connect()) throw new InvalidOperationException("No desktop host.");
            var empty = CompositionState(handles);
            if (empty != before) throw new InvalidOperationException("A host with no Fence changed Explorer composition.");
            result.Add("No Fence: " + empty);
            host.SetOutlineSelectionForProbe(outline);
            var fence = new DesktopOrganizer.Core.Models.Fence(Guid.NewGuid(), "Composition probe", 1900, 1000, 300, 200);
            var layout = FenceSurfaceLayout.Clip(fence, host.GetArea(), host.GetScale(fence)) ?? throw new InvalidOperationException("No probe area.");
            host.Update(fence, layout, host.GetScale(fence), null);
            var idle = CompositionState(handles);
            if (((uint)NativeMethods.SendMessage(handles.IconList, 0x1037, 0, 0) & 0x10000) == 0)
                throw new InvalidOperationException("Idle icon rendering lost double buffering.");
            var presents = host.BitmapPresentCount;
            result.Add("Fence idle: " + idle);
            trace.Begin();
            host.PrepareNativeSelection();
            var native = CompositionState(handles);
            if (outline)
            {
                var styles = (uint)NativeMethods.SendMessage(handles.IconList, 0x1037, 0, 0);
                var expected = idle.Replace($"styles=0x{styles | 0x10000:X}", $"styles=0x{styles:X}");
                if ((styles & 0x10000) != 0 || native != expected)
                    throw new InvalidOperationException("Outline selection changed more than its double-buffering bit.");
            }
            else if (native != before) throw new InvalidOperationException("Native marquee composition differs from original Explorer.");
            result.Add("Native selection backing: " + native);
            host.PrepareNativeSelection();
            if (CompositionState(handles) != native) throw new InvalidOperationException("Repeated marquee begin changed active composition.");
            host.FinishNativeSelection();
            if (CompositionState(handles) != idle) throw new InvalidOperationException("Marquee completion did not restore idle composition.");
            host.FinishNativeSelection();
            if (CompositionState(handles) != idle || host.BitmapPresentCount != presents)
                throw new InvalidOperationException("Marquee rebuilt Fence bitmaps or changed state after repeated completion.");
            if (outline && trace.Snapshot.Any(p => p.Stage.StartsWith("RedrawWindow", StringComparison.Ordinal)))
                throw new InvalidOperationException("Stable marquee performed an additional composition repaint.");
            result.Add("Fence restored: " + CompositionState(handles));
            if (outline)
            {
                host.PrepareNativeSelection();
                host.SetCompositionForProbe(false);
                host.FinishNativeSelection();
                if (CompositionState(handles) != before)
                    throw new InvalidOperationException("Disabling Fence during selection did not restore Explorer properties.");
                host.SetCompositionForProbe(true);
                host.PrepareNativeSelection(); // Dispose must also restore an unfinished selection.
                result.Add("PASS: double buffering idle/after selection; only its bit changes during selection; 0 extra composition redraws; no Fence bitmap presents; disable during selection; unfinished selection shutdown");
            }
        }
        if (CompositionState(handles) != before) throw new InvalidOperationException("Explorer properties were not restored.");
        result.Add("Shutdown restored: " + before);
        return result;
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
    public static void ShowOwnProbeLayers(Win32DesktopHost host, bool body, bool frame)
    {
        NativeMethods.EnumChildWindows(host.SurfaceParent, (window, _) =>
        {
            if (NativeMethods.ProcessOf(window) == Environment.ProcessId && NativeMethods.GetParent(window) == host.SurfaceParent)
            {
                var name = NativeMethods.ClassOf(window);
                if (name == "DesktopOrganizer.FenceBackground") NativeMethods.ShowWindow(window, body ? 8 : 0);
                if (name == "DesktopOrganizer.FenceSurface") NativeMethods.ShowWindow(window, frame ? 8 : 0);
            }
            return true;
        }, 0);
    }
    public static void SetProbeIconComposition(Win32DesktopHost host, bool enabled) => host.SetCompositionForProbe(enabled);
    public static void SetProbeNativeSelection(Win32DesktopHost host, bool enabled) => host.SetNativeSelectionForProbe(enabled);
    public static void SetProbeOutlineSelection(Win32DesktopHost host, bool enabled) => host.SetOutlineSelectionForProbe(enabled);
    /// <summary>Diagnostic-only reversible modes; caller must dispose its host before trying another mode.</summary>
    public static void SetWallpaperProbeMode(Win32DesktopHost host, string mode)
    {
        var handles = host.Handles ?? throw new InvalidOperationException("No desktop host.");
        if (!new DesktopDiscovery(Microsoft.Extensions.Logging.Abstractions.NullLogger<DesktopDiscovery>.Instance).Validate(handles))
            throw new InvalidOperationException("Desktop host validation failed.");
        var key = mode is "key-only" or "full-key" or "key-transparent";
        host.SetCompositionForProbe(key);
        if (mode is "key-transparent" or "native-transparent")
        {
            NativeMethods.SendMessage(handles.IconList, 0x1036, 0x00400000, 0x00400000);
            if (mode == "native-transparent") NativeMethods.SendMessage(handles.IconList, 0x1001, 0, -1);
            if (!NativeMethods.RedrawWindow(handles.Host, 0, 0, 0x185)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        ShowOwnProbeLayers(host, mode is not "original" and not "header-only" and not "key-only",
            mode is not "original" and not "body-only" and not "key-only");
        host.EnsureStacking();
    }
    /// <summary>Read-only class/PID/style hierarchy; captions and filesystem paths are deliberately omitted.</summary>
    public static IReadOnlyList<string> WallpaperWindowTree(DesktopHandles handles)
    {
        var lines = new List<string>();
        void Add(nint window)
        {
            if (lines.Count >= 200) return;
            var attributes = NativeMethods.GetLayeredWindowAttributes(window, out var key, out var alpha, out var flags)
                ? $"key=0x{key:X}; alpha={alpha}; flags=0x{flags:X}" : "none";
            lines.Add($"HWND=0x{window:X}; parent=0x{NativeMethods.GetParent(window):X}; class={NativeMethods.ClassOf(window)}; PID={NativeMethods.ProcessOf(window)}; visible={NativeMethods.IsWindowVisible(window)}; style=0x{NativeMethods.GetWindowStyle(window, -16):X}; ex=0x{NativeMethods.GetWindowStyle(window, -20):X}; layered={attributes}");
        }
        NativeMethods.EnumWindows((window, _) =>
        {
            if (window == handles.Progman || NativeMethods.ClassOf(window) == "WorkerW")
            {
                Add(window);
                NativeMethods.EnumChildWindows(window, (child, _) => { Add(child); return lines.Count < 200; }, 0);
            }
            return lines.Count < 200;
        }, 0);
        return lines;
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
        NativeMethods.EnumChildWindows(NativeMethods.GetParent(handles.DefView), (window, _) =>
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

    /// <summary>Publishes external filesystem changes for exclusively owned smoke fixtures; never changes files or icon positions.</summary>
    public static void NotifySmokeItemChange(string path, string? renamedTo = null, bool deleted = false)
    {
        var desktop = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        foreach (var item in renamedTo is null ? new[] { path } : new[] { path, renamedTo })
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(Path.GetFullPath(item)), desktop) ||
                !Path.GetFileName(item).StartsWith("DesktopOrganizer-probe-", StringComparison.Ordinal) ||
                !item.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Shell smoke notifications are limited to owned desktop fixture names.");
        SHChangeNotify(renamedTo is not null ? 1 : deleted ? 4 : 2, 0x2005, path, renamedTo); // PATHW | FLUSHNOWAIT
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void SHChangeNotify(int change, uint flags, string before, string? after);

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
