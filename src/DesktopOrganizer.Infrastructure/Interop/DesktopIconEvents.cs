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
    public event Action? StackingChanged;
    public event Action? BackgroundPress;
    public event Action? BackgroundPressClassified;
    public event Action<ExplorerPointerEvent>? Pointer;
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
    public void Dispose()
    {
        _connectionSequence++;
        _marquee.End();
        Volatile.Write(ref _stoppingReceiver, 1);
        _backgroundMouse?.Dispose(); _backgroundMouse = null;
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
}
