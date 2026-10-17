using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.Infrastructure.Interop;

[SupportedOSPlatform("windows")]
public sealed class Win32DesktopHost(DesktopDiscovery discovery, ILogger<Win32DesktopHost> logger, string? presentationRecoveryPath = null,
    ExplorerInteractionTrace? interactionTrace = null) : IDisposable
{
    private const string ClassName = "DesktopOrganizer.FenceSurface", BackgroundClass = "DesktopOrganizer.FenceBackground";
    private static readonly NativeMethods.WindowProc Procedure = WindowProcedure;
    private static bool _registered;
    private readonly Dictionary<Guid, NativeFenceSurface> _windows = [];
    private readonly ILogger<Win32DesktopHost> _logger = logger;
    private static readonly Dictionary<nint, (Win32DesktopHost Host, DesktopFenceInput Input)> Inputs = [];
    private static readonly Dictionary<nint, NativeFenceSurface> PaintSurfaces = [];
    private readonly HashSet<nint> _destroying = [];
    private readonly DesktopIconEvents _iconEvents = new(logger, interactionTrace);
    public ExplorerInteractionTrace Trace { get; } = interactionTrace ?? new(Microsoft.Extensions.Logging.Abstractions.NullLogger<ExplorerInteractionTrace>.Instance);
    private IReadOnlyList<DesktopIcon> _icons = [];
    private bool _stackingDirty;
    private ExplorerIconLayer? _iconLayer;
    private bool _probeCompositionEnabled = true;
    private bool _probeNativeSelectionEnabled = true;
    private bool _outlineMarquee = true;
    private readonly List<DesktopBounds> _damage = [];
    private int _suppressExplorerPaint;
    public long RepaintCount { get; private set; }
    public long BitmapPresentCount => _windows.Values.Sum(s => s.Presents);
    public double LayeredUpdateMilliseconds => _windows.Values.Sum(s => s.LayeredUpdateMilliseconds);
    public long BitmapAllocationCount => _windows.Values.Sum(s => s.BitmapAllocations);
    public long HeightGeometryUpdates => _windows.Values.Sum(s => s.HeightRegionUpdates);
    public double HeightRegionMilliseconds => _windows.Values.Sum(s => s.HeightRegionMilliseconds);
    private nint _dpiContext;
    private DesktopArea _area, _iconArea;
    private bool _interactionEnabled = true;
    // A short drop commit must finish before a new native Fence collapse/drag mutates its proposal.
    // The caller's write-permission state remains independent and survives the scope.
    public bool InteractionEnabled { get => _interactionEnabled && _suppressExplorerPaint == 0; set => _interactionEnabled = value; }
    public bool HasActiveInteraction
    {
        get { foreach (var surface in _windows.Values) if (Inputs.TryGetValue(surface.Frame, out var input) && input.Input.Active) return true; return false; }
    }
    public event EventHandler<FenceInteractionEventArgs>? Interaction;
    public event Action<Guid>? VisualChanged;
    public event Action? IconPositionsChanged;
    public event Action<string, string>? IconRenamed;
    public event Action<string>? IconRemoved;
    public event Action<ExplorerPointerEvent>? ExplorerPointer;
    public event Action<DesktopPoint, bool>? HoverPointerMoved;
    public (DesktopPoint Point, bool OnDesktop) ReadHoverPointer()
    {
        if (Handles is null || !NativeMethods.IsWindow(Handles.IconList) || NativeMethods.ProcessOf(Handles.IconList) != Handles.ProcessId) return (default, false);
        var point = GetPointer();
        var target = NativeMethods.WindowFromPoint(point);
        var onDesktop = target == Handles.IconList || target == Handles.DefView ||
            _windows.Values.Any(surface => (surface.Frame == target || surface.Background == target) && OwnWindow(target));
        return (new(point.X, point.Y), onDesktop);
    }
    private void OnHoverPointerMoved()
    {
        if (HoverPointerMoved is null) return;
        var pointer = ReadHoverPointer();
        HoverPointerMoved.Invoke(pointer.Point, pointer.OnDesktop);
    }
    public bool HasExplorerGesture => _iconEvents.GestureActive;
    public void SetSelectionReader(IDesktopSelectionReader? reader) => _iconEvents.SelectionReader = reader;
    public bool IsNativeSelection => _iconLayer?.NativeSelection == true;
    internal void PrepareNativeSelection() { if (_probeNativeSelectionEnabled) _iconLayer?.BeginNativeSelection(); }
    public void FinishNativeSelection() => _iconLayer?.EndNativeSelection();
    public event Action<Fence>? CollapseRequested;
    internal void NotifyCollapse(Fence fence) => CollapseRequested?.Invoke(fence);
    public IconGridMetrics IconMetrics { get; set; } = IconGridMetrics.Default;
    public DesktopHandles? Handles { get; private set; }
    public nint SurfaceParent => Handles?.Host ?? 0;
    public bool PresentationConnected => Handles is not null && _iconLayer is not null;
    public int SurfaceCount => _windows.Count;
    public string Diagnostics => Handles is null ? discovery.LastDiagnostics : Handles.ToString();
    internal void SetCompositionForProbe(bool enabled) { _probeCompositionEnabled = enabled; _iconLayer?.SetEnabled(enabled && _windows.Count != 0); }
    internal void SetNativeSelectionForProbe(bool enabled) => _probeNativeSelectionEnabled = enabled;
    internal void SetOutlineSelectionForProbe(bool enabled) { _outlineMarquee = enabled; _iconLayer?.SetOutlineForProbe(enabled); }
    public double DpiScale => Handles is null ? 1 : Math.Max(1, NativeMethods.GetDpiForWindow(Handles.Host) / 96.0);
    internal void NotifyInteraction(FenceInteractionEventArgs args)
    {
        if (args.Phase != InteractionPhase.Preview)
            _logger.LogInformation("Desktop interaction {Phase}: Fence={Id}; X={X}; Y={Y}; Width={Width}; Height={Height}",
                args.Phase, args.Fence.Id, args.Fence.X, args.Fence.Y, args.Fence.Width, args.Fence.Height);
        Interaction?.Invoke(this, args);
    }
    internal void NotifyVisualChanged(Guid id) => VisualChanged?.Invoke(id);
    public FenceHit GetHover(Guid id) => _windows.TryGetValue(id, out var surface) && Inputs.TryGetValue(surface.Frame, out var input) ? input.Input.Hover : FenceHit.None;
    public void PollActiveInteraction()
    {
        foreach (var surface in _windows.Values)
            if (Inputs.TryGetValue(surface.Frame, out var input) && input.Input.Active) { input.Input.Poll(); break; }
    }
    public bool IsLeftButtonDown => (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0;
    public bool IsExplorerDragging
    {
        get
        {
            if (Handles is null || !NativeMethods.IsWindow(Handles.IconList)) return false;
            var thread = NativeMethods.GetWindowThreadProcessId(Handles.IconList, out _);
            var info = new NativeMethods.GuiThreadInfo { Size = (uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
            return NativeMethods.GetGUIThreadInfo(thread, ref info) && info.Capture != 0;
        }
    }
    internal NativeMethods.Point GetPointer()
    {
        using var dpi = new DesktopDpiScope(_dpiContext);
        Check(NativeMethods.GetCursorPos(out var point));
        return point;
    }
    public IReadOnlyList<DesktopArea> GetMonitorAreas()
    {
        RequireHost();
        return DesktopDiagnostics.MonitorAreas();
    }
    public double GetScale(Fence fence)
    {
        nint monitor;
        using (var dpi = new DesktopDpiScope(_dpiContext))
            monitor = NativeMethods.MonitorFromPoint(new((int)Math.Clamp(fence.X + fence.Width / 2, int.MinValue, int.MaxValue), (int)Math.Clamp(fence.Y, int.MinValue, int.MaxValue)), 2);
        using var queryDpi = new DesktopDpiScope(-2);
        return NativeMethods.GetDpiForMonitor(monitor, 0, out var x, out _) == 0 ? Math.Max(1, x / 96.0) : DpiScale;
    }
    public bool Connect()
    {
        if (HasActiveInteraction) return Handles is not null;
        var found = discovery.Find();
        var lost = false;
        foreach (var surface in _windows.Values) if (!OwnWindow(surface.Frame) || !OwnWindow(surface.Background)) { lost = true; break; }
        if (found != Handles || lost) { Clear(); _iconLayer?.Dispose(); _iconLayer = null; Handles = found; }
        if (Handles is null) { _iconEvents.Connect(null); return false; }
        _iconLayer ??= new ExplorerIconLayer(Handles, discovery, _logger, presentationRecoveryPath, _probeCompositionEnabled && _windows.Count != 0, Trace, _outlineMarquee);
        _iconEvents.Changed -= OnIconsChanged;
        _iconEvents.Changed += OnIconsChanged;
        _iconEvents.Renamed -= OnIconRenamed;
        _iconEvents.Renamed += OnIconRenamed;
        _iconEvents.Removed -= OnIconRemoved;
        _iconEvents.Removed += OnIconRemoved;
        _iconEvents.StackingChanged -= OnStackingChanged;
        _iconEvents.StackingChanged += OnStackingChanged;
        _iconEvents.BackgroundPress -= OnBackgroundPress;
        _iconEvents.BackgroundPress += OnBackgroundPress;
        _iconEvents.BackgroundPressClassified -= OnBackgroundPressClassified;
        _iconEvents.BackgroundPressClassified += OnBackgroundPressClassified;
        _iconEvents.Pointer -= OnExplorerPointer;
        _iconEvents.Pointer += OnExplorerPointer;
        _iconEvents.PointerMoved -= OnHoverPointerMoved;
        _iconEvents.PointerMoved += OnHoverPointerMoved;
        try { _iconEvents.Connect(Handles); }
        catch (Exception error) { _logger.LogWarning(error, "Desktop input/drop notifications unavailable; observation fallback remains active and connection will retry"); }
        _dpiContext = NativeMethods.GetWindowDpiAwarenessContext(SurfaceParent);
        using var dpi = new DesktopDpiScope(_dpiContext);
        Check(NativeMethods.GetClientRect(SurfaceParent, out var rect));
        var origin = new NativeMethods.Point(); Check(NativeMethods.ClientToScreen(SurfaceParent, ref origin));
        _area = new(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
        Check(NativeMethods.GetClientRect(Handles.IconList, out rect)); origin = new(); Check(NativeMethods.ClientToScreen(Handles.IconList, ref origin));
        _iconArea = new(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return true;
    }
    public DesktopArea GetArea() => _area;
    public bool Contains(Guid id) => _windows.TryGetValue(id, out var surface) && OwnWindow(surface.Frame) && OwnWindow(surface.Background);
    public void Update(Fence fence, FenceSurfaceLayout layout, double scale, byte[]? header)
    {
        using var dpi = new DesktopDpiScope(_dpiContext);
        if (!_windows.TryGetValue(fence.Id, out var surface))
        {
            var handles = RequireHost(); RegisterClass();
            var background = Create(BackgroundClass, "DesktopOrganizer Fence background", SurfaceParent, true);
            nint frame = 0;
            try
            {
                frame = Create(ClassName, $"DesktopOrganizer Fence: {fence.Name}", SurfaceParent, false);
                surface = new NativeFenceSurface(frame, background);
            }
            catch { if (frame != 0) NativeMethods.DestroyWindow(frame); NativeMethods.DestroyWindow(background); throw; }
            _windows.Add(fence.Id, surface);
            Inputs.Add(frame, (this, new DesktopFenceInput(this, frame) { Fence = fence, Scale = scale }));
            PaintSurfaces.Add(frame, surface); PaintSurfaces.Add(background, surface);
            _stackingDirty = true;
            _logger.LogInformation("Created desktop Fence {Id}: frame=0x{Frame:X}; background=0x{Background:X}; Parent=0x{Parent:X}", fence.Id, frame, background, SurfaceParent);
        }
        if (!NativeMethods.IsWindow(surface.Frame) || !NativeMethods.IsWindow(surface.Background) ||
            NativeMethods.GetParent(surface.Frame) != SurfaceParent || NativeMethods.GetParent(surface.Background) != SurfaceParent)
            throw new InvalidOperationException("Desktop surface lost its validated parent.");
        if (Inputs.TryGetValue(surface.Frame, out var input))
        {
            if (input.Input.Fence.Name != fence.Name) Check(NativeMethods.SetWindowText(surface.Frame, $"DesktopOrganizer Fence: {fence.Name}"));
            input.Input.Fence = fence; input.Input.Scale = scale;
        }
        AddDamage(surface.Layout);
        surface.Update(fence, layout, scale, header, GetHover(fence.Id), _area);
        Trace.Mark("Fence.ULW/update");
        AddDamage(layout);
        if (_stackingDirty) EnsureStacking();
        // Establish the transparent ListView backing after our layered children and their Z-order exist.
        _iconLayer?.SetEnabled(_probeCompositionEnabled);
    }
    public void Remove(Guid id)
    {
        if (!_windows.TryGetValue(id, out var surface)) return;
        if (Inputs.TryGetValue(surface.Frame, out var entry)) entry.Input.Cancel();
        _windows.Remove(id); Inputs.Remove(surface.Frame);
        AddDamage(surface.Layout);
        PaintSurfaces.Remove(surface.Frame); PaintSurfaces.Remove(surface.Background);
        if (OwnWindow(surface.Frame) && !_destroying.Contains(surface.Frame)) Check(NativeMethods.DestroyWindow(surface.Frame));
        if (OwnWindow(surface.Background) && !_destroying.Contains(surface.Background)) Check(NativeMethods.DestroyWindow(surface.Background));
        _destroying.Remove(surface.Frame); _destroying.Remove(surface.Background);
        surface.Dispose();
        if (_windows.Count == 0) _iconLayer?.SetEnabled(false);
        _logger.LogInformation("Destroyed desktop Fence {Id}", id);
    }
    public HeightAnimation BeginHeightAnimation(Fence fence, FenceSurfaceLayout maximum, double scale, byte[] header)
    {
        if (!_windows.TryGetValue(fence.Id, out var surface) || !Contains(fence.Id))
            throw new InvalidOperationException("Animation requires existing validated Fence windows.");
        using var dpi = new DesktopDpiScope(_dpiContext);
        surface.PrepareHeightAnimation(fence, maximum, scale, header, GetHover(fence.Id), _area);
        SyncFenceModel(fence);
        return new HeightAnimation(this, surface, _dpiContext, SurfaceParent);
    }
    public sealed class HeightAnimation : IDisposable
    {
        private readonly Win32DesktopHost _host;
        private readonly NativeFenceSurface _surface;
        private readonly nint _dpiContext, _parent;
        private bool _disposed;
        internal HeightAnimation(Win32DesktopHost host, NativeFenceSurface surface, nint dpiContext, nint parent)
        { _host = host; _surface = surface; _dpiContext = dpiContext; _parent = parent; }
        public void Present(FenceSurfaceLayout layout)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_host.SurfaceParent != _parent || !OwnWindow(_surface.Frame) || !OwnWindow(_surface.Background) ||
                NativeMethods.GetParent(_surface.Frame) != _parent || NativeMethods.GetParent(_surface.Background) != _parent)
                throw new InvalidOperationException("Animation lost its validated desktop host.");
            using var dpi = new DesktopDpiScope(_dpiContext);
            _surface.PresentHeight(layout); // Compositor updates only: no ListView damage, invalidation or SetRedraw.
        }
        public void Dispose() { if (_disposed) return; _disposed = true; _surface.EndHeightAnimation(); }
    }
    public void Clear() { foreach (var id in _windows.Keys.ToArray()) Remove(id); }
    public void SetIcons(IReadOnlyList<DesktopIcon> icons)
    {
        _icons = icons;
    }
    public void ReconcileExplorerPresentation()
    {
        if (HasActiveInteraction || HasExplorerGesture || IsExplorerDragging || _suppressExplorerPaint != 0) return;
        _iconLayer?.EnsureComposition();
        EnsureStacking();
    }
    public void SyncFenceModel(Fence fence)
    {
        if (_windows.TryGetValue(fence.Id, out var surface) && Inputs.TryGetValue(surface.Frame, out var input)) input.Input.Fence = fence;
    }
    public IDisposable SuppressExplorerPainting()
    {
        _suppressExplorerPaint++;
        return new PaintScope(this);
    }
    private sealed class PaintScope(Win32DesktopHost host) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; host._damage.Clear(); host._suppressExplorerPaint--; }
    }
    public void SetIconTargets(IReadOnlyList<DesktopIconTarget> targets)
    {
        foreach (var target in targets)
        {
            var icon = _icons.FirstOrDefault(i => FenceIconLayout.IdentityComparer.Equals(i.StableId, target.StableId));
            if (icon is not null)
            {
                _damage.Add(icon.Bounds);
                _damage.Add(icon.Bounds with { X = icon.Bounds.X + target.Position.X - icon.Position.X, Y = icon.Bounds.Y + target.Position.Y - icon.Position.Y });
            }
        }
    }
    public void EnsureStacking()
    {
        if (Handles is null) return;
        var listSeen = false;
        var misplacedFrames = new HashSet<nint>();
        var misplacedBodies = new HashSet<nint>();
        var iconLayer = Handles.IconList;
        for (var sibling = NativeMethods.GetWindow(SurfaceParent, 5); sibling != 0; sibling = NativeMethods.GetWindow(sibling, 2))
        {
            if (sibling == iconLayer) listSeen = true;
            if (PaintSurfaces.TryGetValue(sibling, out var surface))
            {
                if (sibling == surface.Frame && listSeen) misplacedFrames.Add(sibling);
                if (sibling == surface.Background && !listSeen) misplacedBodies.Add(sibling);
            }
        }
        _stackingDirty = false;
        var count = misplacedFrames.Count + misplacedBodies.Count;
        if (count == 0) return;
        var batch = NativeMethods.BeginDeferWindowPos(count);
        if (batch == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        // A ListView focus/reorder must not also reorder every already-correct body HWND.
        // Cached layered images are composed in the new order without a GDI erase/repaint cascade.
        const uint flags = 0x1B; // NOMOVE | NOSIZE | NOACTIVATE | NOREDRAW
        foreach (var surface in _windows.Values)
        {
            if (misplacedBodies.Contains(surface.Background) && OwnWindow(surface.Background))
                batch = NativeMethods.DeferWindowPos(batch, surface.Background, iconLayer, 0, 0, 0, 0, flags);
            if (batch == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        nint above = 0;
        foreach (var surface in _windows.Values.Reverse())
        {
            if (misplacedFrames.Contains(surface.Frame) && OwnWindow(surface.Frame))
                batch = NativeMethods.DeferWindowPos(batch, surface.Frame, above, 0, 0, 0, 0, flags);
            if (batch == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            above = surface.Frame;
        }
        Check(NativeMethods.EndDeferWindowPos(batch));
        Trace.Mark("Stacking.corrected.frames", misplacedFrames.Count);
        Trace.Mark("Stacking.corrected.bodies", misplacedBodies.Count);
    }
    /// <summary>Finish the single Explorer dirty-region repaint following a surface/icon presentation.</summary>
    public void FlushPainting()
    {
        FlushDamage();
    }
    private void AddDamage(FenceSurfaceLayout layout)
    { if (layout.Width > 0 && layout.Height > 0) _damage.Add(new(layout.X, layout.Y, layout.Width, layout.Height)); }
    public void FlushDamage()
    {
        if (_suppressExplorerPaint != 0) { _damage.Clear(); return; }
        if (_damage.Count == 0) return;
        if (Handles is null || !NativeMethods.IsWindow(Handles.IconList) || NativeMethods.ProcessOf(Handles.IconList) != Handles.ProcessId) { _damage.Clear(); return; }
        var rectangles = DesktopDamage.Clip(_damage, _iconArea); _damage.Clear();
        if (rectangles.Count == 0) return;
        var region = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        if (region == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var part = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (part == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            foreach (var rect in rectangles)
            {
                Check(NativeMethods.SetRectRgn(part, rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height));
                if (NativeMethods.CombineRgn(region, region, part, 2) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            Check(NativeMethods.RedrawWindow(Handles.IconList, 0, region, 0x145)); // INVALIDATE | ERASE | UPDATENOW | NOCHILDREN. Clear old glyphs with the transparent key, never repaint the whole desktop.
            Trace.Mark("RedrawWindow.ListView.damage");
            RepaintCount++;
        }
        finally { if (part != 0) NativeMethods.DeleteObject(part); NativeMethods.DeleteObject(region); }
    }
    private void OnBackgroundPress() => PrepareNativeSelection();
    private void OnBackgroundPressClassified() { if (_probeNativeSelectionEnabled) _iconLayer?.PrepareBackgroundSelection(); }
    private void OnExplorerPointer(ExplorerPointerEvent input)
    {
        // A rapid icon press may arrive before the previous marquee's release
        // observation. Restore buffering first; a newly classified background
        // press will prepare outline mode again before forwarded native DOWN.
        if (input.Phase == ExplorerPointerPhase.Pressed) FinishNativeSelection();
        ExplorerPointer?.Invoke(input);
    }
    private void OnStackingChanged()
    {
        try
        {
            // Z-order is presentation only: do not wait for post-drop/polling to restore headers
            // when Explorer raises its ListView on focus. No icon move/layout/save is performed.
            EnsureStacking();
        }
        catch (Exception error) { _logger.LogWarning(error, "Desktop stacking notification interrupted"); }
    }
    private void OnIconsChanged()
    {
        try { IconPositionsChanged?.Invoke(); }
        catch (Exception error) { _logger.LogWarning(error, "Desktop notification interrupted"); }
    }
    private void OnIconRenamed(string before, string after) => IconRenamed?.Invoke(before, after);
    private void OnIconRemoved(string identity) => IconRemoved?.Invoke(identity);
    public void Dispose() { _iconEvents.Dispose(); Clear(); _iconLayer?.Dispose(); _iconLayer = null; Handles = null; }
    private DesktopHandles RequireHost()
    {
        if (Handles is null || !discovery.Validate(Handles)) throw new InvalidOperationException("Desktop host no longer valid.");
        return Handles;
    }
    private static nint Create(string className, string title, nint parent, bool transparent)
    {
        var window = NativeMethods.CreateWindowEx(NativeMethods.Layered | NativeMethods.NoActivate | NativeMethods.ToolWindow | (transparent ? NativeMethods.Transparent : 0),
            className, title, NativeMethods.Child | NativeMethods.Visible | (transparent ? NativeMethods.Disabled : 0), 0, 0, 1, 1, parent, 0, NativeMethods.GetModuleHandle(null), 0);
        if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        return window;
    }
    private static bool OwnWindow(nint window) => NativeMethods.IsWindow(window) && NativeMethods.ProcessOf(window) == (uint)Environment.ProcessId &&
        NativeMethods.ClassOf(window) is ClassName or BackgroundClass;
    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    private static void RegisterClass()
    {
        if (_registered) return;
        foreach (var name in new[] { ClassName, BackgroundClass })
        {
            var definition = new NativeMethods.WindowClass { Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(), Procedure = Procedure, Instance = NativeMethods.GetModuleHandle(null), ClassName = name };
            if (NativeMethods.RegisterClassEx(ref definition) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        _registered = true;
    }
    private static nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (PaintSurfaces.TryGetValue(window, out var surface))
            {
                if (message == 0xF) { surface.Paint(window); return 0; }
                if (message == 0x14) return 1;
                if (window == surface.Background && message == 0x84) return -1;
            }
            if (Inputs.TryGetValue(window, out var entry))
            {
                if (message == 0x82) { Inputs.Remove(window); entry.Host._destroying.Add(window); }
                try { if (entry.Input.Process(message, lParam, out var result)) return result; }
                catch (Exception exception) { entry.Host._logger.LogError(exception, "Desktop mouse interaction failed"); entry.Input.Cancel(); }
            }
        }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); }
        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }
}
