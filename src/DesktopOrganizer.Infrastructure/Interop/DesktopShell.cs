using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DesktopOrganizer.Infrastructure.Interop;

[SupportedOSPlatform("windows")]
public static class DesktopShell
{
    /// <summary>Supported Shell automation equivalent of Show Desktop. No private messages.</summary>
    public static void ShowDesktop()
    {
        var type = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new InvalidOperationException("Shell.Application is unavailable.");
        var shell = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Could not create Shell.Application.");
        try { type.InvokeMember("MinimizeAll", System.Reflection.BindingFlags.InvokeMethod, null, shell, null); }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
}
