using System.Runtime.InteropServices;
using System.Text;

namespace DesktopOrganizer.Infrastructure.Interop;

internal static class NativeMethods
{
    internal const uint Child = 0x40000000, Visible = 0x10000000, Disabled = 0x08000000;
    internal const uint Layered = 0x00080000, Transparent = 0x20, NoActivate = 0x08000000, ToolWindow = 0x80;
    internal const uint NoActivation = 0x10, ShowWindowFlag = 0x40;
    internal delegate bool EnumProc(nint window, nint parameter);
    internal delegate nint WindowProc(nint window, uint message, nint wParam, nint lParam);
    internal delegate bool MonitorProc(nint monitor, nint dc, ref Rect bounds, nint parameter);
    internal delegate void WinEventProc(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    internal delegate nint MouseHookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] internal struct MouseHookData
    { public Point Position; public uint MouseData, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct Message
    { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Position; public uint Private; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint SetWindowsHookEx(int kind, MouseHookProc callback, nint module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool PeekMessage(out Message message, nint window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool PostThreadMessage(uint thread, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWinEventHook(uint minimum, uint maximum, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
    [StructLayout(LayoutKind.Sequential)] internal struct GuiThreadInfo
    { public uint Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretBounds; }
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [StructLayout(LayoutKind.Sequential)] internal struct RawInputDevice { public ushort Page, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)] internal struct RawInputHeader { public uint Type, Size; public nint Device, Parameter; }
    [StructLayout(LayoutKind.Sequential)] internal struct RawMouse { public ushort Flags, Padding, Buttons, Data; public uint RawButtons; public int X, Y; public uint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct RawInput { public RawInputHeader Header; public RawMouse Mouse; }
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetRawInputData(nint input, uint command, out RawInput data, ref uint size, uint headerSize);
    [StructLayout(LayoutKind.Sequential)] internal struct PaintStruct
    {
        public nint Dc; public int Erase; public Rect Paint; public int Restore, Incremental;
        public long Reserved1, Reserved2, Reserved3, Reserved4;
    }
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("user32.dll")] internal static extern int GetWindowRgn(nint window, nint region);
    [DllImport("gdi32.dll")] internal static extern uint GetRegionData(nint region, uint size, [Out] byte[]? data);
    [DllImport("gdi32.dll")] internal static extern nint ExtCreateRegion(nint transform, uint size, byte[] data);
    [DllImport("shell32.dll")] internal static extern nint ILClone(nint pidl);
    [DllImport("gdi32.dll")] internal static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetRectRgn(nint region, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] internal static extern int CombineRgn(nint destination, nint first, nint second, int operation);
    [DllImport("gdi32.dll")] internal static extern nint CreateSolidBrush(uint color);
    [DllImport("user32.dll")] internal static extern int FillRect(nint dc, ref Rect rect, nint brush);
    [DllImport("user32.dll")] internal static extern int FrameRect(nint dc, ref Rect rect, nint brush);
    [DllImport("user32.dll")] internal static extern nint BeginPaint(nint window, out PaintStruct paint);
    [DllImport("user32.dll")] internal static extern bool EndPaint(nint window, ref PaintStruct paint);
    [DllImport("user32.dll")] internal static extern bool InvalidateRect(nint window, nint rect, bool erase);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RedrawWindow(nint window, nint rect, nint region, uint flags);
    [DllImport("user32.dll")] internal static extern bool UpdateWindow(nint window);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern uint GetMessagePos();
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool ScreenToClient(nint window, ref Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("gdi32.dll")] internal static extern int StretchDIBits(nint dc, int x, int y, int width, int height,
        int sourceX, int sourceY, int sourceWidth, int sourceHeight, byte[] pixels, ref BitmapInfo info, uint usage, uint operation);
    [DllImport("user32.dll")] internal static extern nint BeginDeferWindowPos(int count);
    [DllImport("user32.dll")] internal static extern nint DeferWindowPos(nint batch, nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool EndDeferWindowPos(nint batch);
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseTracking { public uint Size, Flags; public nint Window; public uint HoverTime; }
    [DllImport("user32.dll")] internal static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] internal static extern nint GetCapture();
    [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint LoadCursor(nint instance, nint cursor);
    [DllImport("user32.dll")] internal static extern bool TrackMouseEvent(ref MouseTracking tracking);
    [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorProc callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] internal struct Size { public int Width, Height; public Size(int width, int height) { Width = width; Height = height; } }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct Blend { public byte Operation, Flags, Alpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important;
        public uint Color;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct WindowClass
    {
        public uint Size, Style;
        public WindowProc Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? Menu;
        public string ClassName;
        public nint SmallIcon;
    }

    [DllImport("user32.dll")] internal static extern nint GetShellWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumProc callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumChildWindows(nint parent, EnumProc callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern nint GetParent(nint window);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr64(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong32(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static extern nint SetWindowLongPtr64(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] internal static extern int SetWindowLong32(nint window, int index, int value);
    internal static nint SetWindowStyle(nint window, int index, nint value) => IntPtr.Size == 8 ? SetWindowLongPtr64(window, index, value) : SetWindowLong32(window, index, (int)value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    internal static nint GetWindowStyle(nint window, int index) => IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : GetWindowLong32(window, index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint window, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowText(nint window, string text);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] internal static extern nint GetDC(nint window);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint window, StringBuilder name, int maxCount);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] internal static extern nint GetWindowDpiAwarenessContext(nint window);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool UnregisterClass(string className, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateLayeredWindow(nint window, nint destinationDc, ref Point destination, ref Size size, nint sourceDc, ref Point source, uint colorKey, ref Blend blend, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(nint dc);

    internal static string ClassOf(nint window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }
    internal static uint ProcessOf(nint window) { GetWindowThreadProcessId(window, out var id); return id; }
}
