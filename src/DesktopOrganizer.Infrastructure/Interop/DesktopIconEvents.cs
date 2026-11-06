using System.ComponentModel;
using DesktopOrganizer.Core.Desktop;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.Infrastructure.Interop;

/// <summary>Read-only native gesture and Shell item notifications; Explorer owns all mouse delivery.</summary>
[SupportedOSPlatform("windows")]
internal sealed class DesktopIconEvents : IDisposable
{
    private readonly NativeMethods.WinEventProc _callback;
    private readonly ILogger _logger;
    private readonly ExplorerInteractionTrace? _trace;
    private readonly List<nint> _hooks = [];
    private readonly List<(uint Registration, nint Pidl)> _shellNotifications = [];
    private DesktopHandles? _handles;
    private const string ReceiverClass = "DesktopOrganizer.IconDropNotifications";
    private static readonly NativeMethods.WindowProc ReceiverProcedure = OnMessage;
    private static readonly Dictionary<nint, DesktopIconEvents> Receivers = [];
    private static bool _registered;
    private nint _receiver;
    private bool _desktopPress;
    private NativeMethods.Point _pressPoint;
    private bool _dragNotified;
    private int _dragX, _dragY;
    private const uint ShellChanged = 0x8003;
    public IDesktopSelectionReader? SelectionReader { get; set; }
    public bool GestureActive => _desktopPress;
    public event Action? Changed;
    public event Action<string, string>? Renamed;
    public event Action<string>? Removed;
    public event Action? StackingChanged;
    public event Action<ExplorerPointerEvent>? Pointer;
    public event Action? PointerMoved;
    public DesktopIconEvents(ILogger logger, ExplorerInteractionTrace? trace = null) { _callback = OnEvent; _logger = logger; _trace = trace; }
    public void Connect(DesktopHandles? handles)
    {
        if (_handles == handles && (handles is null || _receiver != 0)) return;
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
            foreach (var range in new[] { (0x8u, 0x9u), (0xEu, 0xFu), (0x800Bu, 0x800Cu), (0x8004u, 0x8004u), (0x8000u, 0x8001u) })
            {
                var hook = NativeMethods.SetWinEventHook(range.Item1, range.Item2, 0, _callback, handles.ProcessId, 0, 2); // OUTOFCONTEXT | SKIPOWNPROCESS
                if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                _hooks.Add(hook);
            }
            RegisterShellNotifications();
        }
        catch { Dispose(); throw; }
    }
    private static nint OnMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == ShellChanged && Receivers.TryGetValue(window, out var shellReceiver))
        {
            shellReceiver.OnShellChanged(wParam, unchecked((uint)(long)lParam)); return 0;
        }
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
                    receiver.PointerMoved?.Invoke();
                }
            }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        }
        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }
    private void OnRawMouse(ushort buttons, NativeMethods.Point point)
    {
        if (_handles is null) return;
        if ((buttons & 1) != 0)
        {
            var target = NativeMethods.WindowFromPoint(point);
            var onDesktop = target == _handles.IconList || target == _handles.DefView;
            if (onDesktop) BeginPress(point);
        }
        if (_desktopPress && !_dragNotified &&
            (Math.Abs((long)point.X - _pressPoint.X) > _dragX || Math.Abs((long)point.Y - _pressPoint.Y) > _dragY)) NotifyDrag();
        if ((buttons & 2) != 0 && _desktopPress) // RI_MOUSE_LEFT_BUTTON_UP
        {
            _desktopPress = false;
            var target = NativeMethods.WindowFromPoint(point);
            var desktopTarget = target == _handles.IconList || target == _handles.DefView ||
                NativeMethods.GetParent(target) == _handles.DefView && NativeMethods.ClassOf(target) == "DesktopOrganizer.FenceSurface";
            Pointer?.Invoke(new(ExplorerPointerPhase.Released, new(point.X, point.Y), false, desktopTarget));
            Changed?.Invoke();
        }
    }
    private void BeginPress(NativeMethods.Point point)
    {
        _trace?.Begin();
        _desktopPress = true; _pressPoint = point; _dragNotified = false;
        _dragX = Math.Max(4, NativeMethods.GetSystemMetrics(68)); _dragY = Math.Max(4, NativeMethods.GetSystemMetrics(69));
        var selection = SelectionReader?.ReadSelectionAsync(new(point.X, point.Y));
        Pointer?.Invoke(new(ExplorerPointerPhase.Pressed, new(point.X, point.Y), false, true) { SelectionSnapshot = selection });
    }
    private void NotifyDrag()
    {
        if (!_desktopPress || _dragNotified) return;
        _dragNotified = true;
        Pointer?.Invoke(new(ExplorerPointerPhase.DragStarted, new(_pressPoint.X, _pressPoint.Y), false, true));
    }
    private void OnEvent(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
    {
        var handles = _handles;
        if (handles is null || !NativeMethods.IsWindow(handles.IconList)) return;
        if (kind >= 0x8000 && window != handles.IconList) return;
        if (kind is 0x8000 or 0x8001 or 0x800C && (objectId != -4 || childId <= 0)) return; // OBJID_CLIENT item only
        // OLE owns capture on its own window during a drag; its CAPTUREEND marks the end of Explorer's drop.
        if (kind < 0x8000 && !(kind is 0xF or 0x9 && _dragNotified) &&
            window != handles.IconList && window != handles.DefView && window != handles.Progman && window != handles.WorkerW) return;
        // Subscribers enqueue work and do not make Shell/COM calls from the reentrant callback.
        if (kind == 0x8004) StackingChanged?.Invoke();
        if (kind == 0xE) NotifyDrag();
        if (kind == 0xF) _trace?.Mark("Explorer.OLE.end");
        if (kind == 0x9 && _dragNotified) _trace?.Mark("Explorer.capture.end");
        if (kind == 0xF && _desktopPress && _dragNotified && (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0)
        {
            // Escape/right-button cancellation can finish OLE while the physical left button is still held.
            _desktopPress = false;
            Pointer?.Invoke(new(ExplorerPointerPhase.Cancelled, default, false, false));
        }
        Changed?.Invoke();
    }
    private void RegisterShellNotifications()
    {
        foreach (var folder in new[] { new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"), new Guid("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25") })
        {
            var id = folder;
            nint pidl = 0;
            try
            {
                Marshal.ThrowExceptionForHR(SHGetKnownFolderIDList(ref id, 0, 0, out pidl));
                var entry = new ShellChangeEntry { Pidl = pidl };
                const int events = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x1000 | 0x2000 | 0x20000; // rename/create/delete/update immediate desktop children
                var registration = SHChangeNotifyRegister(_receiver, 0x8003, events, ShellChanged, 1, ref entry); // INTERRUPT | SHELL | NEWDELIVERY
                if (registration == 0) throw new InvalidOperationException("Shell desktop membership registration failed.");
                _shellNotifications.Add((registration, pidl)); pidl = 0;
            }
            catch (Exception error) { _logger.LogWarning("Shell desktop membership notifications unavailable ({ExceptionType})", error.GetType().Name); }
            finally { if (pidl != 0) Marshal.FreeCoTaskMem(pidl); }
        }
    }
    private void OnShellChanged(nint change, uint process)
    {
        var token = SHChangeNotification_Lock(change, process, out var pidls, out var kind);
        string? before = null, after = null;
        try
        {
            if (token == 0) return;
            if (pidls != 0 && (kind & (0x1 | 0x4 | 0x10 | 0x20000)) != 0)
            {
                var oldPidl = Marshal.ReadIntPtr(pidls); var newPidl = Marshal.ReadIntPtr(pidls, IntPtr.Size);
                if (oldPidl != 0) before = ShellFolderView.Name(oldPidl, 0x80028000);
                if ((kind & (0x1 | 0x20000)) != 0 && newPidl != 0) after = ShellFolderView.Name(newPidl, 0x80028000);
            }
        }
        catch (Exception error) { _logger.LogWarning("Shell desktop membership notification unavailable ({ExceptionType})", error.GetType().Name); }
        finally { if (token != 0) SHChangeNotification_Unlock(token); }
        try
        {
            if (before is not null && after is not null) Renamed?.Invoke(before, after);
            if (before is not null && (kind & (0x4 | 0x10)) != 0) Removed?.Invoke(before);
            Changed?.Invoke();
        }
        catch (Exception error) { _logger.LogWarning("Shell desktop membership subscriber interrupted ({ExceptionType})", error.GetType().Name); }
    }
    public void Dispose()
    {
        foreach (var notification in _shellNotifications)
        {
            SHChangeNotifyDeregister(notification.Registration);
            Marshal.FreeCoTaskMem(notification.Pidl);
        }
        _shellNotifications.Clear();
        if (_receiver != 0)
        {
            NativeMethods.RegisterRawInputDevices([new() { Page = 1, Usage = 2, Flags = 1 }], 1, (uint)Marshal.SizeOf<NativeMethods.RawInputDevice>());
            Receivers.Remove(_receiver); NativeMethods.DestroyWindow(_receiver); _receiver = 0;
        }
        foreach (var hook in _hooks) NativeMethods.UnhookWinEvent(hook);
        _hooks.Clear(); _handles = null; _desktopPress = false;
        GC.KeepAlive(_callback);
    }
    [StructLayout(LayoutKind.Sequential)] private struct ShellChangeEntry { public nint Pidl; public int Recursive; }
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderIDList(ref Guid folder, uint flags, nint token, out nint pidl);
    [DllImport("shell32.dll")] private static extern uint SHChangeNotifyRegister(nint window, int sources, int events, uint message, int count, ref ShellChangeEntry entry);
    [DllImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SHChangeNotifyDeregister(uint registration);
    [DllImport("shell32.dll")] private static extern nint SHChangeNotification_Lock(nint change, uint process, out nint pidls, out int kind);
    [DllImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SHChangeNotification_Unlock(nint token);
}
