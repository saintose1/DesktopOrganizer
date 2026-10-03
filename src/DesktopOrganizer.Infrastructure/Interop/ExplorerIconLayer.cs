using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using DesktopOrganizer.Core.Desktop;

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
    private readonly ExplorerInteractionTrace? _trace;
    private State? _original;
    private bool _nativeSelection;
    private bool _outlineMarquee;
    private bool _fenceCompositionEnabled;
    public bool NativeSelection => _nativeSelection;
    private sealed record State(long Window, uint ExplorerPid, uint Extended, uint ListStyles, uint Background, uint TextBackground,
        bool RegionCaptured = false, byte[]? WindowRegion = null);
    public ExplorerIconLayer(DesktopHandles handles, DesktopDiscovery discovery, ILogger logger, string? recoveryPath, bool enabled = true, ExplorerInteractionTrace? trace = null, bool outlineMarquee = true)
    {
        _handles = handles; _discovery = discovery; _logger = logger; _recoveryPath = recoveryPath; _trace = trace;
        Recover();
        var list = handles.IconList;
        var original = new State((long)list, handles.ProcessId, (uint)NativeMethods.GetWindowStyle(list, -20),
            (uint)NativeMethods.SendMessage(list, 0x1037, 0, 0), (uint)NativeMethods.SendMessage(list, 0x1000, 0, 0),
            (uint)NativeMethods.SendMessage(list, 0x1025, 0, 0));
        if ((original.Extended & NativeMethods.Layered) != 0) throw new InvalidOperationException("Desktop icon layer is already controlled by another layered renderer.");
        Save(original); _original = original;
        _fenceCompositionEnabled = enabled;
        _outlineMarquee = outlineMarquee;
        try
        {
            if (enabled)
            {
                ApplyFenceComposition();
                Redraw(parent: true); // Once when establishing the lease.
            }
            _logger.LogInformation("Explorer icon layer: HWND=0x{Hwnd:X}; Fence composition={Enabled}; scoped native outline marquee={Outline}; icon double buffering enabled", list, enabled, outlineMarquee);
        }
        catch { Dispose(); throw; }
    }
    public void SetEnabled(bool enabled)
    {
        if (enabled == _fenceCompositionEnabled || _original is null) return;
        _fenceCompositionEnabled = enabled;
        if (_nativeSelection) return;
        if (enabled) { ApplyFenceComposition(); Redraw(parent: false); }
        else if (_discovery.Validate(_handles)) Restore(_original);
    }
    internal void SetOutlineForProbe(bool outline)
    {
        if (outline == _outlineMarquee) return;
        EndNativeSelection();
        _outlineMarquee = outline;
        if (_fenceCompositionEnabled) { ApplyFenceComposition(); Redraw(parent: false); }
    }
    private void ApplyFenceComposition()
    {
        if (_original is null || !_discovery.Validate(_handles)) throw new InvalidOperationException("Desktop composition lease lost its Explorer host.");
        var list = _handles.IconList;
        // Icon/OLE rendering keeps native double buffering. Only a classified empty
        // background press disables the alpha marquee bit, before Explorer sees DOWN.
        // Layered/color-key backing itself never changes during outline selection.
        NativeMethods.SendMessage(list, 0x1036, (nint)Mask, (nint)DoubleBuffer);
        NativeMethods.SendMessage(list, 0x1001, 0, (nint)Key);
        NativeMethods.SendMessage(list, 0x1026, 0, -1);
        Marshal.SetLastPInvokeError(0);
        var extended = (uint)NativeMethods.GetWindowStyle(list, -20);
        NativeMethods.SetWindowStyle(list, -20, (nint)(extended | NativeMethods.Layered));
        if (Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!NativeMethods.SetLayeredWindowAttributes(list, Key, 255, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void BeginNativeSelection()
    {
        if (!_fenceCompositionEnabled || _nativeSelection || _original is null || !_discovery.Validate(_handles)) return;
        if (_outlineMarquee)
        {
            NativeMethods.SendMessage(_handles.IconList, 0x1036, (nint)DoubleBuffer, 0);
            _nativeSelection = true;
            _trace?.Mark("Composition.marquee.outline.begin");
            return;
        }
        _trace?.Mark("Composition.native.begin");
        // Alpha marquee cannot blend against a color-key backing bitmap: blended key pixels become opaque.
        // Give Explorer its original wallpaper/WM_PRINTCLIENT composition for the whole native selection.
        Restore(_original, parentRepaint: false); _nativeSelection = true;
        _logger.LogDebug("Explorer marquee: original native wallpaper composition enabled; no color-key alpha backing");
    }
    public void EndNativeSelection()
    {
        if (!_nativeSelection || _original is null) return;
        _nativeSelection = false;
        if (_outlineMarquee)
        {
            _trace?.Mark("Composition.marquee.outline.end");
            if (_discovery.Validate(_handles))
            {
                if (_fenceCompositionEnabled) NativeMethods.SendMessage(_handles.IconList, 0x1036, (nint)DoubleBuffer, (nint)DoubleBuffer);
                else Restore(_original, parentRepaint: false);
            }
            return;
        }
        _trace?.Mark("Composition.native.end");
        if (_fenceCompositionEnabled)
        {
            ApplyFenceComposition();
            Redraw(parent: false);
        }
        _logger.LogDebug("Explorer marquee completed; Fence composition restored");
    }
    internal void PrepareBackgroundSelection()
    {
        // The legacy A/B guard still waits for actual movement; production only
        // changes DOUBLEBUFFER, with unchanged key/alpha/parent/Z-order.
        if (_outlineMarquee) BeginNativeSelection();
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
    private void Restore(State state, bool parentRepaint = true)
    {
        var list = _handles.IconList;
        var extended = (uint)NativeMethods.GetWindowStyle(list, -20);
        NativeMethods.SetWindowStyle(list, -20, (nint)((extended & ~NativeMethods.Layered) | (state.Extended & NativeMethods.Layered)));
        NativeMethods.SendMessage(list, 0x1036, (nint)Mask, (nint)(state.ListStyles & Mask));
        NativeMethods.SendMessage(list, 0x1001, 0, (nint)state.Background);
        NativeMethods.SendMessage(list, 0x1026, 0, (nint)state.TextBackground);
        // Compatibility with recovery journals from the previous animation implementation.
        // Current transitions never change the Explorer window region.
        if (state.RegionCaptured) DesktopIconRegionState.Restore(list, state.WindowRegion);
        Redraw(parentRepaint);
    }
    private void Redraw(bool parent)
    {
        _trace?.Mark(parent ? "RedrawWindow.composition.parent" : "RedrawWindow.composition.ListView");
        if (!NativeMethods.RedrawWindow(parent ? _handles.Host : _handles.IconList, 0, 0, parent ? 0x185u : 0x145u))
            throw new Win32Exception(Marshal.GetLastWin32Error());
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
