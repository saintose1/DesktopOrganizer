using System.ComponentModel;
using System.Diagnostics;
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
    private FenceAppearance? _appearance;
    private HeightResources? _heightResources;
    public double LayeredUpdateMilliseconds => _body.PresentMilliseconds + _frame.PresentMilliseconds;
    public long BitmapAllocations => _body.Allocations + _frame.Allocations;
    public long HeightRegionUpdates { get; private set; }
    public double HeightRegionMilliseconds { get; private set; }
    public NativeFenceSurface(nint frame, nint background) { Frame = frame; Background = background; }
    public void Update(Fence fence, FenceSurfaceLayout layout, double scale, byte[]? header, FenceHit hover, DesktopArea area, bool prepareOnly = false)
    {
        var old = Layout; var previousHeader = _headerHeight;
        var appearanceChanged = _appearance != fence.Appearance;
        var bodyChanged = _appearance?.BodyColor != fence.Appearance.BodyColor || _appearance?.BodyOpacity != fence.Appearance.BodyOpacity;
        _appearance = fence.Appearance;
        Layout = layout;
        _headerHeight = Math.Clamp((int)Math.Ceiling(fence.Y + FenceInteractionGeometry.HeaderHeight * scale - layout.Y), 0, layout.Height);
        if (header is not null) _header = header;
        var resized = old.Width != layout.Width || old.Height != layout.Height;
        var borderWidth = (int)Math.Ceiling(FenceInteractionGeometry.Border * scale);
        if (resized || previousHeader != _headerHeight || bodyChanged)
        {
            var bodyPixel = AppearancePixels.FromHex(fence.Appearance.BodyColor, fence.Appearance.BodyOpacity);
            _body.Ensure(layout.Width, layout.Height, 0);
            for (var y = 0; y < layout.Height; y++)
            {
                var row = _body.Row(y, layout.Width); row.Clear();
                if (y >= _headerHeight && y < layout.Height - borderWidth && layout.Width > 2 * borderWidth)
                    row.Slice(borderWidth, layout.Width - 2 * borderWidth).Fill(bodyPixel);
            }
            if (!prepareOnly) { _body.Present(Background, layout.X, layout.Y, layout.Width, layout.Height); Presents++; }
        }
        var repaint = resized || header is not null || previousHeader != _headerHeight || appearanceChanged;
        if (!prepareOnly && (resized || previousHeader != _headerHeight)) SetFrameRegion(layout, borderWidth);
        if (repaint)
        {
            _frame.Ensure(layout.Width, layout.Height, 0);
            var edge = AppearancePixels.FromHex(fence.Appearance.BorderColor, fence.Appearance.BorderOpacity);
            if (edge == 0) edge = 0x01000000; // Retain the existing invisible resize hit area.
            if (hover is not FenceHit.None and not FenceHit.Header and not FenceHit.Collapse)
                edge = AppearancePixels.FromHex(fence.Appearance.BorderColor, Math.Min(100, fence.Appearance.BorderOpacity + 15));
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
            if (!prepareOnly) { _frame.Present(Frame, layout.X, layout.Y, layout.Width, layout.Height); Presents++; }
        }
        if (!prepareOnly && !resized && (old.X != layout.X || old.Y != layout.Y))
        {
            var batch = NativeMethods.BeginDeferWindowPos(2);
            const uint flags = 0x114; // NOZORDER | NOACTIVATE | NOCOPYBITS. Layered images never enter a GDI parent backing store.
            if (batch != 0) batch = NativeMethods.DeferWindowPos(batch, Background, 0, layout.X - area.X, layout.Y - area.Y, layout.Width, layout.Height, flags);
            if (batch != 0) batch = NativeMethods.DeferWindowPos(batch, Frame, 0, layout.X - area.X, layout.Y - area.Y, layout.Width, layout.Height, flags);
            if (batch == 0 || !NativeMethods.EndDeferWindowPos(batch)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
    public void PrepareHeightAnimation(Fence fence, FenceSurfaceLayout maximum, double scale, byte[] header, FenceHit hover, DesktopArea area)
    {
        EndHeightAnimation();
        var old = Layout;
        _appearance = null; // Rasterize the maximum-size cache once, without changing native geometry.
        Update(fence, maximum, scale, header, hover, area, prepareOnly: true);
        Layout = old;
        var edge = AppearancePixels.FromHex(fence.Appearance.BorderColor, fence.Appearance.BorderOpacity);
        if (edge == 0) edge = 0x01000000;
        if (hover is not FenceHit.None and not FenceHit.Header and not FenceHit.Collapse)
            edge = AppearancePixels.FromHex(fence.Appearance.BorderColor, Math.Min(100, fence.Appearance.BorderOpacity + 15));
        _heightResources = new(maximum, (int)Math.Ceiling(FenceInteractionGeometry.Border * scale), edge,
            AppearancePixels.FromHex(fence.Appearance.BodyColor, fence.Appearance.BodyOpacity));
        if (_heightResources.InnerRegion == 0) { EndHeightAnimation(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }
    public void PresentHeight(FenceSurfaceLayout layout)
    {
        var resources = _heightResources ?? throw new InvalidOperationException("Height animation is not prepared.");
        if (layout.Width != resources.Maximum.Width || layout.X != resources.Maximum.X || layout.Y != resources.Maximum.Y ||
            layout.Height < 1 || layout.Height > resources.Maximum.Height) throw new InvalidOperationException("Animation left its cached geometry.");
        if (Layout == layout) return; // Subpixel progress must not submit duplicate integer geometry.
        var width = layout.Width; var border = resources.Border;
        if (resources.LastBottom >= 0)
        {
            var row = _frame.Row(resources.LastBottom, width); row.Clear();
            if (resources.LastBottom < _headerHeight && _header is not null)
                MemoryMarshal.Cast<byte, uint>(_header.AsSpan(resources.LastBottom * width * 4, width * 4)).CopyTo(row);
            else { row[..Math.Min(border, width)].Fill(0x01000000); row[Math.Max(0, width - border)..].Fill(0x01000000); }
            row[0] = row[^1] = resources.Edge;
            if (resources.LastBottom == 0 || resources.LastBottom == resources.Maximum.Height - 1) row.Fill(resources.Edge);
            for (var y = Math.Max(_headerHeight, resources.LastBottom + 1 - border); y <= resources.LastBottom; y++) RestoreBodyRow(y);
        }
        for (var y = Math.Max(_headerHeight, layout.Height - border); y < layout.Height; y++) _body.Row(y, width).Clear();
        _frame.Row(layout.Height - 1, width).Fill(resources.Edge);
        resources.LastBottom = layout.Height - 1;
        var regionStarted = Stopwatch.GetTimestamp();
        SetFrameRegion(layout, border, resources.InnerRegion);
        HeightRegionMilliseconds += Stopwatch.GetElapsedTime(regionStarted).TotalMilliseconds; HeightRegionUpdates++;
        _body.Present(Background, layout.X, layout.Y, width, layout.Height);
        _frame.Present(Frame, layout.X, layout.Y, width, layout.Height);
        Presents += 2; Layout = layout;
        void RestoreBodyRow(int y)
        {
            var row = _body.Row(y, width); row.Clear();
            if (y < resources.Maximum.Height - border && width > 2 * border) row.Slice(border, width - 2 * border).Fill(resources.Body);
        }
    }
    public void EndHeightAnimation() { _heightResources?.Dispose(); _heightResources = null; }
    private void SetFrameRegion(FenceSurfaceLayout layout, int border, nint cachedInner = 0)
    {
        // Per-pixel alpha affects composition/hit testing, but Explorer's CLIPSIBLINGS uses the HWND region.
        // A full rectangular frame would prevent the real ListView from painting icons in its transparent content.
        var outer = NativeMethods.CreateRectRgn(0, 0, layout.Width, layout.Height);
        var inner = cachedInner == 0 ? NativeMethods.CreateRectRgn(0, 0, 0, 0) : cachedInner;
        try
        {
            if (outer == 0 || inner == 0 || !NativeMethods.SetRectRgn(inner, Math.Min(border, layout.Width), _headerHeight,
                Math.Max(Math.Min(border, layout.Width), layout.Width - border), Math.Max(_headerHeight, layout.Height - border)) ||
                NativeMethods.CombineRgn(outer, outer, inner, 4) == 0 || NativeMethods.SetWindowRgn(Frame, outer, false) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            outer = 0;
        }
        finally { if (outer != 0) NativeMethods.DeleteObject(outer); if (cachedInner == 0 && inner != 0) NativeMethods.DeleteObject(inner); }
    }
    public void Paint(nint window)
    {
        NativeMethods.BeginPaint(window, out var paint);
        NativeMethods.EndPaint(window, ref paint); // ULW owns the pixels. Never BitBlt a frame into Explorer's redirected parent.
    }
    public void Dispose() { EndHeightAnimation(); _body.Dispose(); _frame.Dispose(); }
    private sealed class HeightResources(FenceSurfaceLayout maximum, int border, uint edge, uint body) : IDisposable
    {
        public readonly FenceSurfaceLayout Maximum = maximum;
        public readonly int Border = border;
        public readonly uint Edge = edge, Body = body;
        public int LastBottom = -1;
        public readonly nint InnerRegion = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        public void Dispose() { if (InnerRegion != 0) NativeMethods.DeleteObject(InnerRegion); }
    }
}
