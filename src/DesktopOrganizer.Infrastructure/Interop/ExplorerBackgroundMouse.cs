using System.Runtime.InteropServices;

namespace DesktopOrganizer.Infrastructure.Interop;

/// <summary>Hands genuine background mouse input back to the existing ListView after color-key hit testing skips it.</summary>
internal sealed class ExplorerBackgroundMouse(DesktopHandles handles)
{
    private readonly BackgroundMouseRoute _route = new();

    public bool Observe(ushort buttons, NativeMethods.Point screen, bool leftDown)
    {
        if (!NativeMethods.IsWindow(handles.IconList) || NativeMethods.ProcessOf(handles.IconList) != handles.ProcessId ||
            NativeMethods.GetParent(handles.IconList) != handles.DefView)
        { _route.Reset(); return false; }

        var thread = NativeMethods.GetWindowThreadProcessId(handles.IconList, out _);
        var explorer = new NativeMethods.GuiThreadInfo { Size = (uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        var foreground = new NativeMethods.GuiThreadInfo { Size = (uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        if (!NativeMethods.GetGUIThreadInfo(thread, ref explorer) || !NativeMethods.GetGUIThreadInfo(0, ref foreground))
        { _route.Reset(); return false; }

        var client = screen;
        if (!NativeMethods.ScreenToClient(handles.IconList, ref client) || !NativeMethods.GetClientRect(handles.IconList, out var bounds))
        { _route.Reset(); return false; }
        var inside = client.X >= bounds.Left && client.X < bounds.Right && client.Y >= bounds.Top && client.Y < bounds.Bottom;
        // Only the validated Explorer parent is eligible. Never forward input from a Fence grip, an icon, or another app.
        var background = inside && NativeMethods.WindowFromPoint(screen) == handles.DefView &&
            ((uint)NativeMethods.GetWindowStyle(handles.IconList, -20) & NativeMethods.Layered) != 0;
        var message = _route.Observe(buttons, background, explorer.Capture != 0 || foreground.Capture != 0, leftDown);
        if (message == 0) return false;
        if (!MouseMessagePoint.TryPack(client.X, client.Y, out var parameter)) { _route.Reset(); return false; }
        var keys = (message == 0x202 ? 0 : 1) |
            ((NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0 ? 4 : 0) |
            ((NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0 ? 8 : 0);
        // Asynchronous: ListView may enter its native modal selection loop on DOWN.
        // Do not SendMessage across threads, SetCapture here, draw a rectangle, or duplicate captured input.
        if (!NativeMethods.PostMessage(handles.IconList, message, keys, parameter))
        { _route.Reset(); return false; }
        return message == 0x201;
    }
}

/// <summary>Only the initial background press and pre-capture tail need forwarding; Explorer owns the gesture thereafter.</summary>
internal sealed class BackgroundMouseRoute
{
    private bool _pending;
    public void Reset() => _pending = false;
    public uint Observe(ushort buttons, bool background, bool captured, bool leftDown)
    {
        if (captured) { Reset(); return 0; }
        if ((buttons & 1) != 0)
        {
            _pending = background;
            return _pending ? 0x201u : 0;
        }
        if (!_pending) return 0;
        if ((buttons & 2) != 0) { Reset(); return 0x202; }
        if (!leftDown) { Reset(); return 0; }
        return buttons == 0 ? 0x200u : 0;
    }
}

internal static class MouseMessagePoint
{
    public static bool TryPack(int x, int y, out nint parameter)
    {
        parameter = 0;
        if (x is < short.MinValue or > short.MaxValue || y is < short.MinValue or > short.MaxValue) return false;
        parameter = unchecked((int)((uint)(ushort)x | ((uint)(ushort)y << 16)));
        return true;
    }
    public static NativeMethods.Point Unpack(uint parameter) => new(unchecked((short)parameter), unchecked((short)(parameter >> 16)));
}
