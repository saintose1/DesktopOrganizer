using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Infrastructure.Interop;

/// <summary>One mouse gesture per native surface; no rendering or persistence responsibilities.</summary>
[SupportedOSPlatform("windows")]
internal sealed class DesktopFenceInput(Win32DesktopHost host, nint window)
{
    public Fence Fence { get; set; } = null!;
    public double Scale { get; set; } = 1;
    public FenceHit Hover { get; private set; }
    private Fence? _original;
    private FenceHit _gesture;
    private NativeMethods.Point _start;
    private double _gestureScale;
    private IReadOnlyList<DesktopArea> _monitors = [];
    private IconGridMetrics _metrics;
    private bool _buttonPressed;
    public bool Active => _original is not null || _buttonPressed;

    public bool Process(uint message, nint parameter, out nint result)
    {
        result = 0;
        switch (message)
        {
            case 0x84:
                // Use the queried screen point, not the current cursor. Content must never claim HTCLIENT.
                result = Fence is null || Hit(MouseMessagePoint.Unpack(unchecked((uint)(long)parameter))) == FenceHit.None ? -1 : 1;
                return true; // HTTRANSPARENT / HTCLIENT; the native region contains only header + borders.
            case 0x21: result = 3; return true; // MA_NOACTIVATE
            case 0x20: SetCursor(); result = 1; return true;
            case 0x200: // WM_MOUSEMOVE
                Poll();
                var track = new NativeMethods.MouseTracking { Size = (uint)Marshal.SizeOf<NativeMethods.MouseTracking>(), Flags = 2, Window = window };
                NativeMethods.TrackMouseEvent(ref track);
                return true;
            case 0x2A3: SetHover(FenceHit.None); return true; // WM_MOUSELEAVE
            case 0x201: Begin(); return true;
            case 0x202: Finish(); return true;
            case 0x215: // WM_CAPTURECHANGED
            case 0x1F: // WM_CANCELMODE
            case 0x82: // WM_NCDESTROY (Explorer can destroy its children)
                Cancel(); return false;
            default: return false;
        }
    }

    private FenceHit Hit(NativeMethods.Point pointer) => FenceInteractionGeometry.HitTest(Fence, pointer.X - Fence.X, pointer.Y - Fence.Y, Scale);
    private void Begin()
    {
        if (!host.InteractionEnabled || Active || Fence is null) return;
        _start = host.GetPointer();
        _gestureScale = Scale;
        _gesture = Hit(_start);
        if (_gesture == FenceHit.None) return;
        if (_gesture == FenceHit.Collapse) _buttonPressed = true;
        else
        {
            _original = Fence;
            _monitors = host.GetMonitorAreas();
            _metrics = host.IconMetrics;
            host.NotifyInteraction(new(Fence, InteractionPhase.Started) { Hit = _gesture });
        }
        NativeMethods.SetCapture(window);
        if (NativeMethods.GetCapture() != window) Cancel();
    }

    public void Poll()
    {
        if (Fence is null) return;
        var pointer = host.GetPointer();
        if (Active)
        {
            if (NativeMethods.GetCapture() != window || (NativeMethods.GetAsyncKeyState(0x1B) & 0x8000) != 0) { Cancel(); return; }
            if ((NativeMethods.GetAsyncKeyState(1) & 0x8000) == 0) { Finish(); return; }
            Preview(pointer);
        }
        SetHover(Hit(pointer));
        SetCursor();
    }

    private void Preview(NativeMethods.Point pointer)
    {
        if (_original is null) return;
        var next = FenceInteractionGeometry.MoveOrResize(_original, _gesture, pointer.X - _start.X, pointer.Y - _start.Y, _gestureScale, _monitors);
        next = FenceIconLayout.ConstrainResize(next, _original, _gesture, _metrics, _gestureScale);
        if (next != Fence) host.NotifyInteraction(new(next, InteractionPhase.Preview) { Hit = _gesture });
    }

    private void Finish()
    {
        if (!Active) return;
        var pointer = host.GetPointer();
        Preview(pointer); // Include the final position even if no last WM_MOUSEMOVE arrived.
        var original = _original;
        var toggle = _buttonPressed && Hit(pointer) == FenceHit.Collapse;
        _original = null; _buttonPressed = false;
        if (NativeMethods.GetCapture() == window) NativeMethods.ReleaseCapture();
        if (original is not null) host.NotifyInteraction(new(Fence, InteractionPhase.Completed) { Hit = _gesture });
        else if (toggle)
        {
            host.NotifyCollapse(Fence);
        }
        SetHover(Hit(pointer));
    }

    public void Cancel()
    {
        var original = _original;
        _original = null; _buttonPressed = false;
        if (NativeMethods.GetCapture() == window) NativeMethods.ReleaseCapture();
        if (original is not null) host.NotifyInteraction(new(original, InteractionPhase.Cancelled));
        SetHover(FenceHit.None);
    }

    private void SetHover(FenceHit hit)
    {
        if (hit == Hover) return;
        Hover = hit;
        host.NotifyVisualChanged(Fence.Id);
    }
    private void SetCursor()
    {
        var hit = Active ? _gesture : Hover;
        var cursor = hit switch
        {
            FenceHit.Left or FenceHit.Right => 32644, // IDC_SIZEWE
            FenceHit.Top or FenceHit.Bottom => 32645, // IDC_SIZENS
            FenceHit.Left | FenceHit.Top or FenceHit.Right | FenceHit.Bottom => 32642,
            FenceHit.Right | FenceHit.Top or FenceHit.Left | FenceHit.Bottom => 32643,
            FenceHit.Header => 32646, // IDC_SIZEALL
            FenceHit.Collapse => 32649, // IDC_HAND
            _ => 32512
        };
        NativeMethods.SetCursor(NativeMethods.LoadCursor(0, cursor));
    }
}
