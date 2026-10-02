using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopOrganizer.Infrastructure.Interop;

internal readonly struct DesktopDpiScope : IDisposable
{
    private readonly nint _previous;
    public DesktopDpiScope() : this(-4) { }
    public DesktopDpiScope(nint context)
    {
        _previous = NativeMethods.SetThreadDpiAwarenessContext(context == 0 ? -4 : context);
        if (_previous == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose() => NativeMethods.SetThreadDpiAwarenessContext(_previous);
}
