using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopOrganizer.Infrastructure.Interop;

/// <summary>Out-of-context notifications and raw desktop input; no mouse hook, injected code or subclassing.</summary>
internal sealed class DesktopIconEvents : IDisposable
{
    private readonly NativeMethods.WinEventProc _callback;
    private readonly List<nint> _hooks = [];
    private DesktopHandles? _handles;
    private const string ReceiverClass = "DesktopOrganizer.IconDropNotifications";
    private static readonly NativeMethods.WindowProc ReceiverProcedure = OnMessage;
    private static readonly Dictionary<nint, DesktopIconEvents> Receivers = [];
    private static bool _registered;
    private nint _receiver;
    private bool _desktopPress;
    private ExplorerBackgroundMouse? _backgroundMouse;
    public event Action? Changed;
    public DesktopIconEvents() => _callback = OnEvent;
    public void Connect(DesktopHandles? handles)
    {
        if (_handles == handles) return;
        Dispose(); _handles = handles;
        if (handles is null) return;
        try
        {
            if (!_registered)
            {
                var definition = new NativeMethods.WindowClass { Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(),
                    Procedure = ReceiverProcedure, Instance = NativeMethods.GetModuleHandle(null), ClassName = ReceiverClass };
                if (NativeMethods.RegisterClassEx(ref definition) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                _registered = true;
            }
            _receiver = NativeMethods.CreateWindowEx(0, ReceiverClass, "", 0, 0, 0, 0, 0, -3, 0, NativeMethods.GetModuleHandle(null), 0); // HWND_MESSAGE
            if (_receiver == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Receivers.Add(_receiver, this);
            if (!NativeMethods.RegisterRawInputDevices([new() { Page = 1, Usage = 2, Flags = 0x100, Target = _receiver }], 1,
                    (uint)Marshal.SizeOf<NativeMethods.RawInputDevice>())) throw new Win32Exception(Marshal.GetLastWin32Error());
            _backgroundMouse = new(handles);
            foreach (var range in new[] { (0x8u, 0x9u), (0xEu, 0xFu), (0x800Bu, 0x800Bu), (0x8004u, 0x8004u) })
            {
                var hook = NativeMethods.SetWinEventHook(range.Item1, range.Item2, 0, _callback, handles.ProcessId, 0, 2); // OUTOFCONTEXT | SKIPOWNPROCESS
                if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                _hooks.Add(hook);
            }
        }
        catch { Dispose(); throw; }
    }
    private static nint OnMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == 0xFF && Receivers.TryGetValue(window, out var receiver))
        {
            try
            {
                var size = (uint)Marshal.SizeOf<NativeMethods.RawInput>();
                if (NativeMethods.GetRawInputData(lParam, 0x10000003, out var input, ref size, (uint)Marshal.SizeOf<NativeMethods.RawInputHeader>()) != uint.MaxValue && input.Header.Type == 0)
                {
                    using var dpi = new DesktopDpiScope();
                    // WM_INPUT's queued screen point preserves the press origin even if the cursor has since moved.
                    receiver.OnRawMouse(input.Mouse.Buttons, MouseMessagePoint.Unpack(NativeMethods.GetMessagePos()));
                }
            }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        }
        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }
    private void OnRawMouse(ushort buttons, NativeMethods.Point point)
    {
        if (_handles is null) return;
        var bridged = _backgroundMouse?.Observe(buttons, point, (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0) == true;
        if ((buttons & 1) != 0)
        {
            _desktopPress = bridged || NativeMethods.WindowFromPoint(point) == _handles.IconList;
        }
        if ((buttons & 2) != 0 && _desktopPress) // RI_MOUSE_LEFT_BUTTON_UP
        { _desktopPress = false; Changed?.Invoke(); }
    }
    private void OnEvent(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
    {
        var handles = _handles;
        if (handles is null || !NativeMethods.IsWindow(handles.IconList)) return;
        if (kind >= 0x8000 && window != handles.IconList) return;
        if (kind < 0x8000 && window != handles.IconList && window != handles.DefView && window != handles.Progman && window != handles.WorkerW) return;
        // Subscribers enqueue work and do not make Shell/COM calls from the reentrant callback.
        Changed?.Invoke();
    }
    public void Dispose()
    {
        if (_receiver != 0)
        {
            NativeMethods.RegisterRawInputDevices([new() { Page = 1, Usage = 2, Flags = 1 }], 1, (uint)Marshal.SizeOf<NativeMethods.RawInputDevice>());
            Receivers.Remove(_receiver); NativeMethods.DestroyWindow(_receiver); _receiver = 0;
        }
        foreach (var hook in _hooks) NativeMethods.UnhookWinEvent(hook);
        _hooks.Clear(); _handles = null; _backgroundMouse = null; _desktopPress = false;
        GC.KeepAlive(_callback);
    }
}
