using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.Infrastructure.Interop;

/// <summary>Reversible, documented ListView background transparency. Explorer retains all items and input.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ExplorerIconLayer : IDisposable
{
    private const uint Mask = 0x00410000, DoubleBuffer = 0x00010000, Key = 0x030201;
    private readonly DesktopHandles _handles;
    private readonly DesktopDiscovery _discovery;
    private readonly ILogger _logger;
    private readonly string? _recoveryPath;
    private State? _original;
    private sealed record State(long Window, uint ExplorerPid, uint Extended, uint ListStyles, uint Background, uint TextBackground);
    public ExplorerIconLayer(DesktopHandles handles, DesktopDiscovery discovery, ILogger logger, string? recoveryPath)
    {
        _handles = handles; _discovery = discovery; _logger = logger; _recoveryPath = recoveryPath;
        Recover();
        var list = handles.IconList;
        var original = new State((long)list, handles.ProcessId, (uint)NativeMethods.GetWindowStyle(list, -20),
            (uint)NativeMethods.SendMessage(list, 0x1037, 0, 0), (uint)NativeMethods.SendMessage(list, 0x1000, 0, 0),
            (uint)NativeMethods.SendMessage(list, 0x1025, 0, 0));
        if ((original.Extended & NativeMethods.Layered) != 0) throw new InvalidOperationException("Desktop icon layer is already controlled by another layered renderer.");
        Save(original); _original = original;
        try
        {
            NativeMethods.SendMessage(list, 0x1036, (nint)Mask, (nint)DoubleBuffer);
            NativeMethods.SendMessage(list, 0x1001, 0, (nint)Key);
            NativeMethods.SendMessage(list, 0x1026, 0, -1);
            Marshal.SetLastPInvokeError(0);
            NativeMethods.SetWindowStyle(list, -20, (nint)(original.Extended | NativeMethods.Layered));
            if (Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!NativeMethods.SetLayeredWindowAttributes(list, Key, 255, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            NativeMethods.RedrawWindow(handles.Host, 0, 0, 0x185); // One complete repaint when establishing the lease.
            _logger.LogInformation("Explorer icon layer: HWND=0x{Hwnd:X}; native color-key background; double buffered; Fence bodies below icons", list);
        }
        catch { Dispose(); throw; }
    }
    private void Save(State state)
    {
        if (_recoveryPath is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_recoveryPath)!);
        var temporary = _recoveryPath + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, state); file.Flush(true); }
        File.Move(temporary, _recoveryPath, true);
    }
    private void Recover()
    {
        if (_recoveryPath is null || !File.Exists(_recoveryPath)) return;
        var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_recoveryPath)) ?? throw new InvalidDataException("Invalid desktop presentation recovery file.");
        if (state.Window == (long)_handles.IconList && state.ExplorerPid == _handles.ProcessId && _discovery.Validate(_handles)) Restore(state);
        File.Delete(_recoveryPath);
    }
    private void Restore(State state)
    {
        var list = _handles.IconList;
        var extended = (uint)NativeMethods.GetWindowStyle(list, -20);
        NativeMethods.SetWindowStyle(list, -20, (nint)((extended & ~NativeMethods.Layered) | (state.Extended & NativeMethods.Layered)));
        NativeMethods.SendMessage(list, 0x1036, (nint)Mask, (nint)(state.ListStyles & Mask));
        NativeMethods.SendMessage(list, 0x1001, 0, (nint)state.Background);
        NativeMethods.SendMessage(list, 0x1026, 0, (nint)state.TextBackground);
        NativeMethods.RedrawWindow(_handles.Host, 0, 0, 0x185);
    }
    public void Dispose()
    {
        if (_original is null) return;
        if (_discovery.Validate(_handles)) Restore(_original);
        _original = null;
        if (_recoveryPath is not null && File.Exists(_recoveryPath)) File.Delete(_recoveryPath);
        _logger.LogInformation("Explorer native background properties restored");
    }
}
