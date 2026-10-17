using System.ComponentModel;
using System.Collections.Concurrent;
using DesktopOrganizer.Core.Desktop;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.Infrastructure.Interop;

/// <summary>Out-of-context notifications and raw drop observation; background routing has a separate lifetime.</summary>
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
    private bool _backgroundPress, _dragNotified, _nativeBackground;
    private int _dragX, _dragY;
    private const uint BackgroundDelivery = 0x8002;
    private const uint ShellChanged = 0x8003;
    private readonly ConcurrentQueue<(uint Message, nint Keys, nint Point)> _backgroundDeliveries = new();
    private int _stoppingReceiver;
    private long _connectionSequence;
    private bool _deliveringBackground;
    private Task<DesktopSelection>? _pressSelection;
    private bool _forwardedPressHandled;
    private readonly MarqueeCompositionGate _marquee = new();
    public IDesktopSelectionReader? SelectionReader { get; set; }
    private ExplorerBackgroundMouse? _backgroundMouse;
    public bool GestureActive => _desktopPress || _deliveringBackground || !_backgroundDeliveries.IsEmpty;
    public event Action? Changed;
    public event Action<string, string>? Renamed;
    public event Action<string>? Removed;
    public event Action? StackingChanged;
    public event Action? BackgroundPress;
    public event Action? BackgroundPressClassified;
    public event Action<ExplorerPointerEvent>? Pointer;
    public event Action? PointerMoved;
    public DesktopIconEvents(ILogger logger, ExplorerInteractionTrace? trace = null) { _callback = OnEvent; _logger = logger; _trace = trace; }
    public void Connect(DesktopHandles? handles)
    {
        if (_handles == handles && (handles is null || _backgroundMouse?.IsAvailable == true)) return;
        Dispose(); _handles = handles;
        if (handles is null) return;
        Volatile.Write(ref _stoppingReceiver, 0);
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
            _backgroundMouse = new(handles, _logger, QueueBackgroundDelivery);
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
        if (message == BackgroundDelivery && Receivers.TryGetValue(window, out var target))
        {
            target.DeliverBackground(); return 0;
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
            // Color-key transparent padding inside an actual item also hits DefView. Only Shell hit-test
            // can classify a press as empty space; capture its selection before native motion starts.
            if (onDesktop && !(_nativeBackground && point.X == _pressPoint.X && point.Y == _pressPoint.Y)) BeginPress(point);
        }
        if (_desktopPress && !_backgroundPress && !_dragNotified &&
            (Math.Abs((long)point.X - _pressPoint.X) > _dragX || Math.Abs((long)point.Y - _pressPoint.Y) > _dragY)) NotifyDrag();
        if ((buttons & 2) == 0 && _marquee.Move(new(point.X, point.Y))) BackgroundPress?.Invoke();
        if ((buttons & 2) != 0 && _desktopPress) // RI_MOUSE_LEFT_BUTTON_UP
        {
            _marquee.End();
            _desktopPress = false;
            var target = NativeMethods.WindowFromPoint(point);
            var desktopTarget = target == _handles.IconList || target == _handles.DefView ||
                NativeMethods.GetParent(target) == _handles.DefView && NativeMethods.ClassOf(target) == "DesktopOrganizer.FenceSurface";
            Pointer?.Invoke(new(ExplorerPointerPhase.Released, new(point.X, point.Y), _backgroundPress, desktopTarget));
            _nativeBackground = false;
            Changed?.Invoke();
        }
    }
    private void BeginPress(NativeMethods.Point point)
    {
        _trace?.Begin();
        _desktopPress = true; _pressPoint = point; _backgroundPress = false; _dragNotified = false; _forwardedPressHandled = false;
        _dragX = Math.Max(4, NativeMethods.GetSystemMetrics(68)); _dragY = Math.Max(4, NativeMethods.GetSystemMetrics(69));
        _marquee.Begin(new(point.X, point.Y), _dragX, _dragY);
        _pressSelection = SelectionReader?.ReadSelectionAsync(new(point.X, point.Y));
        Pointer?.Invoke(new(ExplorerPointerPhase.Pressed, new(point.X, point.Y), false, true) { SelectionSnapshot = _pressSelection });
    }
    private void NotifyDrag()
    {
        if (!_desktopPress || _backgroundPress || _dragNotified) return;
        _dragNotified = true;
        Pointer?.Invoke(new(ExplorerPointerPhase.DragStarted, new(_pressPoint.X, _pressPoint.Y), false, true));
    }
    private bool QueueBackgroundDelivery(uint message, nint keys, nint point)
    {
        var receiver = _receiver;
        if (receiver == 0 || Volatile.Read(ref _stoppingReceiver) != 0) return false;
        _backgroundDeliveries.Enqueue((message, keys, point));
        return NativeMethods.PostMessage(receiver, BackgroundDelivery, 0, 0);
    }
    private async void DeliverBackground()
    {
        if (_deliveringBackground) return;
        var handles = _handles;
        if (handles is null || !NativeMethods.IsWindow(handles.IconList) || NativeMethods.ProcessOf(handles.IconList) != handles.ProcessId) return;
        var connection = _connectionSequence;
        _deliveringBackground = true;
        try
        {
            while (_backgroundDeliveries.TryDequeue(out var item))
            {
                try
                {
                    // HWND preparation stays outside the low-level callback and before native DOWN.
                    if (item.Message == 0x201)
                    {
                        _nativeBackground = true;
                        var point = MouseMessagePoint.Unpack(unchecked((uint)(long)item.Point));
                        if (!NativeMethods.ClientToScreen(handles.IconList, ref point)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        if (_pressSelection is null || _forwardedPressHandled || point.X != _pressPoint.X || point.Y != _pressPoint.Y) BeginPress(point);
                        _forwardedPressHandled = true;
                        var selection = _pressSelection is null ? null : await _pressSelection;
                        if (Volatile.Read(ref _stoppingReceiver) != 0 || connection != _connectionSequence) return;
                        // Transparent image/label padding is still a real Explorer item. Do not reset
                        // its composition; reuse the same pre-DOWN selection task for assignment.
                        _backgroundPress = selection?.DraggedItemId is null;
                        // Must precede forwarded DOWN: alpha marquee must not render its
                        // first frame before the outline style is applied. Item presses,
                        // including transparent icon padding, keep double buffering.
                        if (_backgroundPress) BackgroundPressClassified?.Invoke();
                        // A click must not remove/reapply the layered ListView twice. Native marquee
                        // backing is needed only after movement exceeds the system drag threshold.
                        if (_marquee.Classify(_backgroundPress)) BackgroundPress?.Invoke();
                    }
                    if (item.Message == 0x1F)
                    {
                        _marquee.End();
                        _desktopPress = false; _nativeBackground = false;
                        Pointer?.Invoke(new(ExplorerPointerPhase.Cancelled, default, true, false));
                    }
                    if (!NativeMethods.PostMessage(handles.IconList, item.Message, item.Keys, item.Point))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                catch (Exception error)
                {
                    _marquee.End();
                    _desktopPress = false; _nativeBackground = false;
                    _logger.LogWarning(error, "Background press delivery failed; cancelling only our native selection");
                    NativeMethods.PostMessage(handles.IconList, 0x1F, 0, 0);
                    Pointer?.Invoke(new(ExplorerPointerPhase.Cancelled, default, true, false));
                }
            }
        }
        finally
        {
            _deliveringBackground = false;
            if (Volatile.Read(ref _stoppingReceiver) == 0 && !_backgroundDeliveries.IsEmpty)
                NativeMethods.PostMessage(_receiver, BackgroundDelivery, 0, 0);
        }
    }
    private void OnEvent(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
    {
        var handles = _handles;
        if (handles is null || !NativeMethods.IsWindow(handles.IconList)) return;
        if (kind >= 0x8000 && window != handles.IconList) return;
        if (kind is 0x8000 or 0x8001 or 0x800C && (objectId != -4 || childId <= 0)) return; // OBJID_CLIENT item only
        if (kind < 0x8000 && !(kind == 0xF && _dragNotified) &&
            window != handles.IconList && window != handles.DefView && window != handles.Progman && window != handles.WorkerW) return;
        // Subscribers enqueue work and do not make Shell/COM calls from the reentrant callback.
        if (kind == 0x8004) StackingChanged?.Invoke();
        if (kind == 0xE) NotifyDrag();
        if (kind == 0xF) _trace?.Mark("Explorer.OLE.end");
        if (kind == 0xF && _desktopPress && _dragNotified && !_backgroundPress && (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0)
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
        _connectionSequence++;
        _marquee.End();
        Volatile.Write(ref _stoppingReceiver, 1);
        _backgroundMouse?.Dispose(); _backgroundMouse = null;
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
        _hooks.Clear(); _handles = null; _backgroundMouse = null; _desktopPress = false;
        _nativeBackground = false;
        _pressSelection = null;
        while (_backgroundDeliveries.TryDequeue(out _)) { }
        GC.KeepAlive(_callback);
    }
    [StructLayout(LayoutKind.Sequential)] private struct ShellChangeEntry { public nint Pidl; public int Recursive; }
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderIDList(ref Guid folder, uint flags, nint token, out nint pidl);
    [DllImport("shell32.dll")] private static extern uint SHChangeNotifyRegister(nint window, int sources, int events, uint message, int count, ref ShellChangeEntry entry);
    [DllImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SHChangeNotifyDeregister(uint registration);
    [DllImport("shell32.dll")] private static extern nint SHChangeNotification_Lock(nint change, uint process, out nint pidls, out int kind);
    [DllImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SHChangeNotification_Unlock(nint token);
}
