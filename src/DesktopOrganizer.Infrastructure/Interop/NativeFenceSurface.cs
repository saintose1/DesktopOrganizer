using System.ComponentModel;
using System.Runtime.InteropServices;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Infrastructure.Interop;

/// <summary>Two compositor-owned images: uniform body below Explorer; alpha-only frame above Explorer.</summary>
internal sealed class NativeFenceSurface : IDisposable
{
    public nint Frame { get; }
    public nint Background { get; }
    public FenceSurfaceLayout Layout { get; private set; }
    public long Presents { get; private set; }
    private readonly LayeredBitmap _body = new(), _frame = new();
    private byte[]? _header;
    private int _headerHeight;
    private const uint BodyPixel = 0x690A101B;
    public NativeFenceSurface(nint frame, nint background) { Frame = frame; Background = background; }
    public void Update(Fence fence, FenceSurfaceLayout layout, double scale, byte[]? header, FenceHit hover, DesktopArea area)
    {
        var old = Layout; var previousHeader = _headerHeight;
        Layout = layout;
        _headerHeight = Math.Clamp((int)Math.Ceiling(fence.Y + FenceInteractionGeometry.HeaderHeight * scale - layout.Y), 0, layout.Height);
        if (header is not null) _header = header;
        var resized = old.Width != layout.Width || old.Height != layout.Height;
        var borderWidth = (int)Math.Ceiling(FenceInteractionGeometry.Border * scale);
        if (resized || previousHeader != _headerHeight)
        {
            _body.Ensure(layout.Width, layout.Height, 0);
            for (var y = 0; y < layout.Height; y++)
            {
                var row = _body.Row(y, layout.Width); row.Clear();
                if (y >= _headerHeight && y < layout.Height - borderWidth && layout.Width > 2 * borderWidth)
                    row.Slice(borderWidth, layout.Width - 2 * borderWidth).Fill(BodyPixel);
            }
            _body.Present(Background, layout.X, layout.Y, layout.Width, layout.Height); Presents++;
        }
        var repaint = resized || header is not null || previousHeader != _headerHeight;
        if (resized || previousHeader != _headerHeight) SetFrameRegion(layout, borderWidth);
        if (repaint)
        {
            _frame.Ensure(layout.Width, layout.Height, 0);
            var edge = hover is not FenceHit.None and not FenceHit.Header and not FenceHit.Collapse ? 0xFF87CDFFu : 0xFFBED2EBu;
            for (var y = 0; y < layout.Height; y++)
            {
                var row = _frame.Row(y, layout.Width); row.Clear();
                // A barely visible alpha keeps the entire resize grip interactive, not only its 1px outline.
                if (y < borderWidth || y >= layout.Height - borderWidth) row.Fill(0x01000000);
                else { row[..Math.Min(borderWidth, row.Length)].Fill(0x01000000); row[Math.Max(0, row.Length - borderWidth)..].Fill(0x01000000); }
                if (y < _headerHeight && _header is not null)
                    MemoryMarshal.Cast<byte, uint>(_header.AsSpan(y * layout.Width * 4, layout.Width * 4)).CopyTo(row);
                row[0] = edge; row[^1] = edge;
                if (y == 0 || y == layout.Height - 1) row.Fill(edge);
            }
            _frame.Present(Frame, layout.X, layout.Y, layout.Width, layout.Height); Presents++;
        }
        if (!resized && (old.X != layout.X || old.Y != layout.Y))
        {
            var batch = NativeMethods.BeginDeferWindowPos(2);
            const uint flags = 0x114; // NOZORDER | NOACTIVATE | NOCOPYBITS. Layered images never enter a GDI parent backing store.
            if (batch != 0) batch = NativeMethods.DeferWindowPos(batch, Background, 0, layout.X - area.X, layout.Y - area.Y, layout.Width, layout.Height, flags);
            if (batch != 0) batch = NativeMethods.DeferWindowPos(batch, Frame, 0, layout.X - area.X, layout.Y - area.Y, layout.Width, layout.Height, flags);
            if (batch == 0 || !NativeMethods.EndDeferWindowPos(batch)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
    private void SetFrameRegion(FenceSurfaceLayout layout, int border)
    {
        // Per-pixel alpha affects composition/hit testing, but Explorer's CLIPSIBLINGS uses the HWND region.
        // A full rectangular frame would prevent the real ListView from painting icons in its transparent content.
        var outer = NativeMethods.CreateRectRgn(0, 0, layout.Width, layout.Height);
        var inner = NativeMethods.CreateRectRgn(Math.Min(border, layout.Width), _headerHeight,
            Math.Max(Math.Min(border, layout.Width), layout.Width - border), Math.Max(_headerHeight, layout.Height - border));
        try
        {
            if (outer == 0 || inner == 0 || NativeMethods.CombineRgn(outer, outer, inner, 4) == 0 || NativeMethods.SetWindowRgn(Frame, outer, false) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            outer = 0;
        }
        finally { if (outer != 0) NativeMethods.DeleteObject(outer); if (inner != 0) NativeMethods.DeleteObject(inner); }
    }
    public void Paint(nint window)
    {
        NativeMethods.BeginPaint(window, out var paint);
        NativeMethods.EndPaint(window, ref paint); // ULW owns the pixels. Never BitBlt a frame into Explorer's redirected parent.
    }
    public void Dispose() { _body.Dispose(); _frame.Dispose(); }
}
