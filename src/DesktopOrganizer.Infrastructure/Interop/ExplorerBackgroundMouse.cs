using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.Infrastructure.Interop;

/// <summary>Routes a missed background press. Movement always reaches Windows; Explorer owns capture/selection.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ExplorerBackgroundMouse : IDisposable
{
    private const uint DispatchMessage = 0x8001;
    private readonly DesktopHandles _handles;
    private readonly ILogger _logger;
    private readonly BackgroundMouseRoute _route = new();
    private readonly ConcurrentQueue<Delivery> _deliveries = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly NativeMethods.MouseHookProc _callback;
    private readonly Func<uint, nint, nint, bool>? _deliver;
    private uint _threadId;
    private nint _hook;
    private int _stopping, _failed, _forwarded, _failureReported;
    private bool _deliveredPress;
    private readonly record struct Delivery(uint Message, nint Keys, nint Parameter);
    public bool IsAvailable => _thread.IsAlive && Volatile.Read(ref _failed) == 0;
    public bool IsStopped => !_thread.IsAlive;

    public ExplorerBackgroundMouse(DesktopHandles handles, ILogger logger, Func<uint, nint, nint, bool>? deliver = null)
    {
        _handles = handles; _logger = logger; _callback = OnMouse; _deliver = deliver;
        _thread = new Thread(Run) { Name = "DesktopOrganizer background mouse routing", IsBackground = true };
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }
    private void Run()
    {
        try
        {
            using var dpi = new DesktopDpiScope();
            _threadId = NativeMethods.GetCurrentThreadId();
            NativeMethods.PeekMessage(out _, 0, 0, 0, 0); // Establish the queue before publishing readiness.
            _hook = NativeMethods.SetWindowsHookEx(14, _callback, NativeMethods.GetModuleHandle(null), 0); // WH_MOUSE_LL, in this process.
            if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _ready.SetResult();
            while (Volatile.Read(ref _stopping) == 0)
            {
                var result = NativeMethods.GetMessage(out var message, 0, 0, 0);
                if (result == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (result == 0) break;
                if (message.Id == DispatchMessage) DeliverPending();
            }
        }
        catch (Exception error)
        {
            Volatile.Write(ref _failed, 1); _ready.TrySetException(error);
            _logger.LogWarning(error, "Explorer background mouse routing unavailable; ordinary input passes through");
        }
        finally
        {
            if (_hook != 0) NativeMethods.UnhookWindowsHookEx(_hook);
            CancelExplorer();
            _route.Reset(); GC.KeepAlive(_callback);
        }
    }
    private nint OnMouse(int code, nint message, nint data)
    {
        if (code < 0 || Volatile.Read(ref _stopping) != 0 || Volatile.Read(ref _failed) != 0)
            return NativeMethods.CallNextHookEx(_hook, code, message, data);
        try
        {
            var kind = (uint)message;
            // Returning nonzero for WM_MOUSEMOVE also prevents Windows from moving the cursor.
            // Do not forward or suppress motion: native Explorer capture receives the genuine movement.
            if (kind == 0x200) return NativeMethods.CallNextHookEx(_hook, code, message, data);
            if (!_route.Active && kind != 0x201) return NativeMethods.CallNextHookEx(_hook, code, message, data);
            var input = Marshal.PtrToStructure<NativeMethods.MouseHookData>(data);
            if (!ValidList()) { CancelRoute(); return NativeMethods.CallNextHookEx(_hook, code, message, data); }
            var client = input.Position;
            if (!NativeMethods.ScreenToClient(_handles.IconList, ref client))
            { Queue(new(0x1F, 0, 0)); _route.Reset(); return NativeMethods.CallNextHookEx(_hook, code, message, data); }
            var eligible = kind == 0x201 && !_route.Active &&
                NativeMethods.GetClientRect(_handles.IconList, out var bounds) &&
                client.X >= bounds.Left && client.X < bounds.Right && client.Y >= bounds.Top && client.Y < bounds.Bottom &&
                NativeMethods.WindowFromPoint(input.Position) == _handles.DefView &&
                ((uint)NativeMethods.GetWindowStyle(_handles.IconList, -20) & NativeMethods.Layered) != 0;
            var wasActive = _route.Active;
            var decision = _route.Observe(kind, eligible, ReadCapture());
            if (wasActive && kind == 0x202 && decision == BackgroundMouseDecision.Pass) _deliveredPress = false;
            if (decision == BackgroundMouseDecision.Cancel) Queue(new(0x1F, 0, 0)); // WM_CANCELMODE; never fake a drop.
            if (decision is not (BackgroundMouseDecision.Forward or BackgroundMouseDecision.ForwardAndPass))
                return NativeMethods.CallNextHookEx(_hook, code, message, data);
            if (!MouseMessagePoint.TryPack(client.X, client.Y, out var parameter))
            { Queue(new(0x1F, 0, 0)); _route.Reset(); return NativeMethods.CallNextHookEx(_hook, code, message, data); }
            var keys = MouseMessagePoint.Keys(kind,
                (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0, (NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0);
            // No Shell, activation, synchronous cross-process messages, JSON or logging in this callback.
            // Only the missed DOWN can be suppressed. UP always reaches Windows, including its button-state update.
            if (Queue(new(kind, keys, parameter)))
            {
                if (decision == BackgroundMouseDecision.Forward && kind == 0x201) return 1;
            }
            else _route.Reset();
        }
        catch { Volatile.Write(ref _failed, 1); CancelRoute(); } // Fail open; disconnect/reconnect cleans up.
        return NativeMethods.CallNextHookEx(_hook, code, message, data);
    }
    private bool Queue(Delivery delivery)
    {
        _deliveries.Enqueue(delivery);
        if (NativeMethods.PostThreadMessage(_threadId, DispatchMessage, 0, 0)) return true;
        Volatile.Write(ref _failed, 1); return false;
    }
    private void CancelRoute()
    {
        if (_route.Active) Queue(new(0x1F, 0, 0));
        _route.Reset();
    }
    private BackgroundMouseCapture ReadCapture()
    {
        var explorer = new NativeMethods.GuiThreadInfo { Size = (uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        var foreground = new NativeMethods.GuiThreadInfo { Size = (uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        var thread = NativeMethods.GetWindowThreadProcessId(_handles.IconList, out _);
        if (!NativeMethods.GetGUIThreadInfo(thread, ref explorer) || !NativeMethods.GetGUIThreadInfo(0, ref foreground))
            return BackgroundMouseCapture.Other;
        if (explorer.MenuOwner != 0 || foreground.MenuOwner != 0) return BackgroundMouseCapture.Other;
        if ((foreground.Capture != 0 && foreground.Capture != _handles.IconList) ||
            (explorer.Capture != 0 && explorer.Capture != _handles.IconList)) return BackgroundMouseCapture.Other;
        return explorer.Capture == _handles.IconList ? BackgroundMouseCapture.Explorer : BackgroundMouseCapture.None;
    }
    private bool ValidList() => NativeMethods.IsWindow(_handles.IconList) && NativeMethods.IsWindowVisible(_handles.IconList) &&
        NativeMethods.ClassOf(_handles.IconList) == "SysListView32" && NativeMethods.ProcessOf(_handles.IconList) == _handles.ProcessId &&
        NativeMethods.GetParent(_handles.IconList) == _handles.DefView && NativeMethods.IsWindowVisible(_handles.DefView) &&
        NativeMethods.ClassOf(_handles.DefView) == "SHELLDLL_DefView" && NativeMethods.ProcessOf(_handles.DefView) == _handles.ProcessId &&
        NativeMethods.ProcessOf(NativeMethods.GetShellWindow()) == _handles.ProcessId && ValidParent();
    private bool ValidParent()
    {
        var parent = NativeMethods.GetParent(_handles.DefView);
        return NativeMethods.ProcessOf(parent) == _handles.ProcessId &&
            ((parent == _handles.Progman && NativeMethods.ClassOf(parent) == "Progman") ||
             (parent == _handles.WorkerW && NativeMethods.ClassOf(parent) == "WorkerW"));
    }
    private void DeliverPending()
    {
        if (Volatile.Read(ref _failed) != 0 || Volatile.Read(ref _stopping) != 0 || !ValidList())
        {
            if (Volatile.Read(ref _failed) != 0 && Interlocked.Exchange(ref _failureReported, 1) == 0)
                _logger.LogWarning("Explorer background input routing failed; input passes through until reconnection");
            _route.Reset(); while (_deliveries.TryDequeue(out _)) { } CancelExplorer(); return;
        }
        while (_deliveries.TryDequeue(out var delivery))
        {
            if (Volatile.Read(ref _stopping) != 0) break;
            if (delivery.Message == 0x201)
            {
                _forwarded = 0;
                // Normal desktop focus for the user's actual background press, outside the hook.
                // A newer click that ended/cancelled this gesture must retain its application's focus.
                var root = NativeMethods.GetAncestor(_handles.IconList, 2);
                if (_route.Active && Volatile.Read(ref _stopping) == 0 && (root == _handles.Progman || root == _handles.WorkerW) &&
                    NativeMethods.ProcessOf(root) == _handles.ProcessId && NativeMethods.GetCursorPos(out var cursor) &&
                    (NativeMethods.WindowFromPoint(cursor) == _handles.DefView || NativeMethods.WindowFromPoint(cursor) == _handles.IconList) &&
                    NativeMethods.GetAncestor(NativeMethods.GetForegroundWindow(), 2) != root)
                    NativeMethods.SetForegroundWindow(root);
            }
            if (Volatile.Read(ref _stopping) != 0 || !ValidList()) break;
            if (!(_deliver?.Invoke(delivery.Message, delivery.Keys, delivery.Parameter) ??
                NativeMethods.PostMessage(_handles.IconList, delivery.Message, delivery.Keys, delivery.Parameter)))
            {
                var error = Marshal.GetLastWin32Error();
                Volatile.Write(ref _failed, 1); _route.Reset();
                _logger.LogWarning("Could not deliver Explorer background mouse input; Win32 error={Error}", error);
                CancelExplorer(); break;
            }
            _forwarded++;
            if (delivery.Message == 0x201) _deliveredPress = true;
            if (delivery.Message is 0x202 or 0x1F) _deliveredPress = false;
            if (delivery.Message is 0x202 or 0x1F)
                _logger.LogDebug("Explorer background gesture: {Messages} ordered mouse messages; cancelled={Cancelled}", _forwarded, delivery.Message == 0x1F);
        }
    }
    private void CancelExplorer()
    {
        if (_deliveredPress && ValidList())
        {
            if (_deliver?.Invoke(0x1F, 0, 0) != true) NativeMethods.PostMessage(_handles.IconList, 0x1F, 0, 0);
        }
        _deliveredPress = false;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        NativeMethods.PostThreadMessage(_threadId, 0x12, 0, 0); // WM_QUIT wakes the message loop.
        if (!_thread.Join(TimeSpan.FromSeconds(2))) _logger.LogWarning("Explorer background mouse routing thread is still stopping");
    }
}

internal enum BackgroundMouseCapture { None, Explorer, Other }
internal enum BackgroundMouseDecision { Pass, Forward, ForwardAndPass, Cancel }

/// <summary>Only a missed DOWN is replaced. Native movement/release must not be swallowed.</summary>
internal sealed class BackgroundMouseRoute
{
    public bool Active { get; private set; }
    public void Reset() => Active = false;
    public BackgroundMouseDecision Observe(uint message, bool background, BackgroundMouseCapture capture)
    {
        if (message == 0x200) return BackgroundMouseDecision.Pass;
        if (Active && (capture == BackgroundMouseCapture.Other || message is 0x201 or 0x204 or 0x207 or 0x20B))
        { Reset(); return BackgroundMouseDecision.Cancel; }
        if (message == 0x201)
        {
            Active = background && capture == BackgroundMouseCapture.None;
            return Active ? BackgroundMouseDecision.Forward : BackgroundMouseDecision.Pass;
        }
        if (!Active) return BackgroundMouseDecision.Pass;
        if (message == 0x202)
        {
            Reset();
            // Captured Explorer gets the genuine UP. Before capture, post its tail without suppressing button state.
            return capture == BackgroundMouseCapture.Explorer ? BackgroundMouseDecision.Pass : BackgroundMouseDecision.ForwardAndPass;
        }
        return BackgroundMouseDecision.Pass;
    }
}

internal static class MouseMessagePoint
{
    public static int Keys(uint message, bool shift, bool control) =>
        (message == 0x202 ? 0 : 1) | (shift ? 4 : 0) | (control ? 8 : 0);
    public static bool TryPack(int x, int y, out nint parameter)
    {
        parameter = 0;
        if (x is < short.MinValue or > short.MaxValue || y is < short.MinValue or > short.MaxValue) return false;
        parameter = unchecked((int)((uint)(ushort)x | ((uint)(ushort)y << 16)));
        return true;
    }
    public static NativeMethods.Point Unpack(uint parameter) => new(unchecked((short)parameter), unchecked((short)(parameter >> 16)));
}
