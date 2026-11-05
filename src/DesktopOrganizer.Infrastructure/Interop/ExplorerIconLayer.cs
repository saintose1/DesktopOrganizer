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
    private const uint Mask = 0x00410000, NativeTransparentBuffer = 0x00410000, Key = 0x030201;
    private readonly DesktopHandles _handles;
    private readonly DesktopDiscovery _discovery;
    private readonly ILogger _logger;
    private readonly string? _recoveryPath;
    private readonly ExplorerInteractionTrace? _trace;
    private State? _original;
    private bool _nativeSelection;
    private bool _fenceCompositionEnabled;
    public bool NativeSelection => _nativeSelection;
    private sealed record State(long Window, uint ExplorerPid, uint Extended, uint ListStyles, uint Background, uint TextBackground,
        bool RegionCaptured = false, byte[]? WindowRegion = null);
    public ExplorerIconLayer(DesktopHandles handles, DesktopDiscovery discovery, ILogger logger, string? recoveryPath, bool enabled = true, ExplorerInteractionTrace? trace = null)
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
        try
        {
            if (enabled && ApplyFenceComposition())
            {
                Redraw(parent: true); // Once when establishing the lease.
            }
            _logger.LogInformation("Explorer icon layer: HWND=0x{Hwnd:X}; Fence composition={Enabled}; native transparent/double-buffered ListView; no layered color key", list, enabled);
        }
        catch { Dispose(); throw; }
    }
    public void SetEnabled(bool enabled)
    {
        if (enabled == _fenceCompositionEnabled || _original is null) return;
        if (_nativeSelection) { _fenceCompositionEnabled = enabled; return; }
        try
        {
            if (enabled) { if (ApplyFenceComposition()) Redraw(parent: true); }
            else if (_discovery.Validate(_handles)) Restore(_original);
            _fenceCompositionEnabled = enabled;
        }
        catch
        {
            // A failed lease must restore original properties before the next retry.
            if (_discovery.Validate(_handles)) Restore(_original);
            _fenceCompositionEnabled = false;
            throw;
        }
    }
    public void EnsureComposition()
    {
        if (!_fenceCompositionEnabled || _nativeSelection || _original is null) return;
        if (!_discovery.Validate(_handles)) throw new InvalidOperationException("Desktop composition lease lost its Explorer host.");
        if (!EnsureFenceProperties(_handles.IconList)) return;
        Redraw(parent: true);
        _logger.LogInformation("Explorer desktop background properties changed; owned Fence transparency restored");
    }
    internal static bool EnsureFenceProperties(nint list)
    {
        if (((uint)NativeMethods.GetWindowStyle(list, -20) & NativeMethods.Layered) != 0)
            throw new InvalidOperationException("Desktop icon layer is controlled by another layered renderer.");
        if ((uint)NativeMethods.SendMessage(list, 0x1000, 0, 0) == uint.MaxValue &&
            (uint)NativeMethods.SendMessage(list, 0x1025, 0, 0) == uint.MaxValue &&
            ((uint)NativeMethods.SendMessage(list, 0x1037, 0, 0) & Mask) == NativeTransparentBuffer) return false;
        ApplyFenceProperties(list);
        return true;
    }
    private bool ApplyFenceComposition()
    {
        if (_original is null || !_discovery.Validate(_handles)) throw new InvalidOperationException("Desktop composition lease lost its Explorer host.");
        return EnsureFenceProperties(_handles.IconList);
    }
    internal static void ApplyFenceProperties(nint list)
    {
        if (!NativeMethods.IsWindow(list)) throw new Win32Exception(1400);
        if (((uint)NativeMethods.GetWindowStyle(list, -20) & NativeMethods.Layered) != 0)
            throw new InvalidOperationException("Cannot replace another layered renderer.");
        // Keep Explorer's parent-backed alpha composition; a color-key child corrupts its dirty backing regions.
        NativeMethods.SendMessage(list, 0x1036, (nint)Mask, (nint)NativeTransparentBuffer);
        NativeMethods.SendMessage(list, 0x1001, 0, -1);
        NativeMethods.SendMessage(list, 0x1026, 0, -1);
    }
    public void BeginNativeSelection()
    {
        if (!_fenceCompositionEnabled || _nativeSelection || _original is null || !_discovery.Validate(_handles)) return;
        _nativeSelection = true;
        _trace?.Mark("Composition.native.selection.begin");
    }
    public void EndNativeSelection()
    {
        if (!_nativeSelection || _original is null) return;
        _nativeSelection = false;
        _trace?.Mark("Composition.native.selection.end");
        if (!_fenceCompositionEnabled && _discovery.Validate(_handles)) Restore(_original, parentRepaint: false);
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
        var changed = RestoreProperties(list, state.Extended, state.ListStyles, state.Background, state.TextBackground);
        // Compatibility with recovery journals from the previous animation implementation.
        // Current transitions never change the Explorer window region.
        if (state.RegionCaptured) { DesktopIconRegionState.Restore(list, state.WindowRegion); changed = true; }
        if (changed) Redraw(parentRepaint);
    }
    internal static bool RestoreProperties(nint list, uint originalExtended, uint listStyles, uint background, uint textBackground)
    {
        if (!NativeMethods.IsWindow(list)) throw new Win32Exception(1400);
        var extended = (uint)NativeMethods.GetWindowStyle(list, -20);
        if ((extended & NativeMethods.Layered) != 0 &&
            (!NativeMethods.GetLayeredWindowAttributes(list, out var key, out _, out var flags) || key != Key || flags != 1))
            throw new InvalidOperationException("Cannot restore a desktop icon layer owned by another renderer; recovery journal retained.");
        var changed = false;
        // Old color-key journals restore colors before removing the layered backing; healthy native state is untouched.
        if (((uint)NativeMethods.SendMessage(list, 0x1037, 0, 0) & Mask) != (listStyles & Mask))
        { NativeMethods.SendMessage(list, 0x1036, (nint)Mask, (nint)(listStyles & Mask)); changed = true; }
        if ((uint)NativeMethods.SendMessage(list, 0x1000, 0, 0) != background)
        { NativeMethods.SendMessage(list, 0x1001, 0, (nint)background); changed = true; }
        if ((uint)NativeMethods.SendMessage(list, 0x1025, 0, 0) != textBackground)
        { NativeMethods.SendMessage(list, 0x1026, 0, (nint)textBackground); changed = true; }
        if ((extended & NativeMethods.Layered) != (originalExtended & NativeMethods.Layered))
        {
            Marshal.SetLastPInvokeError(0);
            NativeMethods.SetWindowStyle(list, -20, (nint)((extended & ~NativeMethods.Layered) | (originalExtended & NativeMethods.Layered)));
            if (Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            changed = true;
        }
        return changed;
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
