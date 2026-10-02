using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopOrganizer.Infrastructure.Interop;

/// <summary>Reusable, process-local top-down premultiplied DIB. Never shared with Explorer.</summary>
internal sealed unsafe class LayeredBitmap : IDisposable
{
    public nint Dc { get; } = NativeMethods.CreateCompatibleDC(0);
    private nint _bitmap, _previous, _bits;
    private int _width, _height;
    public bool Ensure(int width, int height, uint fill)
    {
        if (Dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (width <= _width && height <= _height) return false;
        var capacityWidth = Math.Max(width, _width * 2);
        var capacityHeight = Math.Max(height, _height * 2);
        if ((long)capacityWidth * capacityHeight > 16_777_216) { capacityWidth = width; capacityHeight = height; }
        if (capacityWidth < 1 || capacityHeight < 1 || (long)capacityWidth * capacityHeight > 16_777_216) throw new InvalidOperationException("Native bitmap safety limit exceeded.");
        var info = new NativeMethods.BitmapInfo { Size = 40, Width = capacityWidth, Height = -capacityHeight, Planes = 1, BitCount = 32 };
        var bitmap = NativeMethods.CreateDIBSection(Dc, ref info, 0, out var bits, 0, 0);
        if (bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var previous = NativeMethods.SelectObject(Dc, bitmap);
        if (previous == 0 || previous == -1) { NativeMethods.DeleteObject(bitmap); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        if (_bitmap == 0) _previous = previous; else NativeMethods.DeleteObject(_bitmap);
        _bitmap = bitmap; _bits = bits; _width = capacityWidth; _height = capacityHeight;
        new Span<uint>((void*)_bits, checked(_width * _height)).Fill(fill);
        return true;
    }
    public Span<uint> Row(int y, int width) => new((void*)(_bits + checked(y * _width * 4)), width);
    public void Present(nint window, int x, int y, int width, int height)
    {
        var destination = new NativeMethods.Point(x, y); var size = new NativeMethods.Size(width, height);
        var source = new NativeMethods.Point(); var blend = new NativeMethods.Blend { Alpha = 255, AlphaFormat = 1 };
        if (!NativeMethods.UpdateLayeredWindow(window, 0, ref destination, ref size, Dc, ref source, 0, ref blend, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose()
    {
        if (_previous != 0) NativeMethods.SelectObject(Dc, _previous);
        if (_bitmap != 0) NativeMethods.DeleteObject(_bitmap);
        if (Dc != 0) NativeMethods.DeleteDC(Dc);
    }
}
