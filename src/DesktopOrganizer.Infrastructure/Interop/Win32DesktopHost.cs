using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.Infrastructure.Interop;

[SupportedOSPlatform("windows")]
public sealed class Win32DesktopHost(DesktopDiscovery discovery, ILogger<Win32DesktopHost> logger, string? presentationRecoveryPath = null) : IDisposable
{
    private const string ClassName = "DesktopOrganizer.FenceSurface", BackgroundClass = "DesktopOrganizer.FenceBackground";
    private static readonly NativeMethods.WindowProc Procedure = WindowProcedure;
    private static bool _registered;
    private readonly Dictionary<Guid, NativeFenceSurface> _windows = [];
    private readonly ILogger<Win32DesktopHost> _logger = logger;
    private static readonly Dictionary<nint, (Win32DesktopHost Host, DesktopFenceInput Input)> Inputs = [];
    private static readonly Dictionary<nint, NativeFenceSurface> PaintSurfaces = [];
    private readonly HashSet<nint> _destroying = [];
    private readonly DesktopIconEvents _iconEvents = new();
    private IReadOnlyList<DesktopIcon> _icons = [];
    private bool _stackingDirty;
    private ExplorerIconLayer? _iconLayer;
    private readonly List<DesktopBounds> _damage = [];
    public long RepaintCount { get; private set; }
    public long BitmapPresentCount => _windows.Values.Sum(s => s.Presents);
    private nint _dpiContext;
    private DesktopArea _area, _iconArea;
    public bool InteractionEnabled { get; set; } = true;
    public bool HasActiveInteraction
    {
        get { foreach (var surface in _windows.Values) if (Inputs.TryGetValue(surface.Frame, out var input) && input.Input.Active) return true; return false; }
    }
    public event EventHandler<FenceInteractionEventArgs>? Interaction;
    public event Action<Guid>? VisualChanged;
    public event Action? IconPositionsChanged;
    public event Action<Fence>? CollapseRequested;
    internal void NotifyCollapse(Fence fence) => CollapseRequested?.Invoke(fence);
    public IconGridMetrics IconMetrics { get; set; } = IconGridMetrics.Default;
    public DesktopHandles? Handles { get; private set; }
    public bool PresentationConnected => Handles is not null && _iconLayer is not null;
    public int SurfaceCount => _windows.Count;
    public string Diagnostics => Handles?.ToString() ?? discovery.LastDiagnostics;
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
        _iconLayer ??= new ExplorerIconLayer(Handles, discovery, _logger, presentationRecoveryPath);
        _iconEvents.Changed -= OnIconsChanged;
        _iconEvents.Changed += OnIconsChanged;
        try { _iconEvents.Connect(Handles); }
        catch (Exception error) { _logger.LogWarning(error, "Desktop WinEvent notifications unavailable; observation fallback remains active"); }
        _dpiContext = NativeMethods.GetWindowDpiAwarenessContext(Handles.Host);
        using var dpi = new DesktopDpiScope(_dpiContext);
        Check(NativeMethods.GetClientRect(Handles.Host, out var rect));
        var origin = new NativeMethods.Point(); Check(NativeMethods.ClientToScreen(Handles.Host, ref origin));
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
            var background = Create(BackgroundClass, "DesktopOrganizer Fence background", handles.Host, true);
            nint frame = 0;
            try
            {
                frame = Create(ClassName, $"DesktopOrganizer Fence: {fence.Name}", handles.Host, false);
                surface = new NativeFenceSurface(frame, background);
            }
            catch { if (frame != 0) NativeMethods.DestroyWindow(frame); NativeMethods.DestroyWindow(background); throw; }
            _windows.Add(fence.Id, surface);
            Inputs.Add(frame, (this, new DesktopFenceInput(this, frame) { Fence = fence, Scale = scale }));
            PaintSurfaces.Add(frame, surface); PaintSurfaces.Add(background, surface);
            _stackingDirty = true;
            _logger.LogInformation("Created desktop Fence {Id}: frame=0x{Frame:X}; background=0x{Background:X}; Parent=0x{Parent:X}", fence.Id, frame, background, handles.Host);
        }
        if (!NativeMethods.IsWindow(surface.Frame) || !NativeMethods.IsWindow(surface.Background) ||
            NativeMethods.GetParent(surface.Frame) != Handles?.Host || NativeMethods.GetParent(surface.Background) != Handles?.Host)
            throw new InvalidOperationException("Desktop surface lost its validated parent.");
        if (Inputs.TryGetValue(surface.Frame, out var input))
        {
            if (input.Input.Fence.Name != fence.Name) Check(NativeMethods.SetWindowText(surface.Frame, $"DesktopOrganizer Fence: {fence.Name}"));
            input.Input.Fence = fence; input.Input.Scale = scale;
        }
        AddDamage(surface.Layout);
        surface.Update(fence, layout, scale, header, GetHover(fence.Id), _area);
        AddDamage(layout);
        if (_stackingDirty) EnsureStacking();
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
        _logger.LogInformation("Destroyed desktop Fence {Id}", id);
    }
    public void Clear() { foreach (var id in _windows.Keys.ToArray()) Remove(id); }
    public void SetIcons(IReadOnlyList<DesktopIcon> icons)
    {
        _icons = icons;
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
        var listSeen = false; var raised = _stackingDirty;
        for (var sibling = NativeMethods.GetWindow(Handles.Host, 5); sibling != 0; sibling = NativeMethods.GetWindow(sibling, 2))
        {
            if (sibling == Handles.IconList) listSeen = true;
            if (PaintSurfaces.TryGetValue(sibling, out var surface) &&
                ((sibling == surface.Frame && listSeen) || (sibling == surface.Background && !listSeen))) { raised = true; break; }
        }
        if (!raised) return;
        // Explorer may raise ListView on a folder-flag change. Correct that event once,
        // rather than perturbing cross-process sibling order on every mouse move.
        foreach (var surface in _windows.Values)
        {
            // Every uniform body is below the transparent Explorer icon layer.
            if (OwnWindow(surface.Background) && NativeMethods.GetParent(surface.Background) == Handles.Host)
                Check(NativeMethods.SetWindowPos(surface.Background, Handles.IconList, 0, 0, 0, 0, 0x13));
        }
        foreach (var surface in _windows.Values)
            if (OwnWindow(surface.Frame)) Check(NativeMethods.SetWindowPos(surface.Frame, 0, 0, 0, 0, 0, 0x13));
        _stackingDirty = false;
        // ULW images are already complete; no parent/GDI repaint cascade after restacking.
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
        if (_damage.Count == 0) return;
        if (Handles is null || !NativeMethods.IsWindow(Handles.IconList) || NativeMethods.ProcessOf(Handles.IconList) != Handles.ProcessId) { _damage.Clear(); return; }
        var rectangles = DesktopDamage.Clip(_damage, _iconArea); _damage.Clear();
        if (rectangles.Count == 0) return;
        var region = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        if (region == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            foreach (var rect in rectangles)
            {
                var part = NativeMethods.CreateRectRgn(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);
                if (part == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { if (NativeMethods.CombineRgn(region, region, part, 2) == 0) throw new Win32Exception(Marshal.GetLastWin32Error()); }
                finally { NativeMethods.DeleteObject(part); }
            }
            Check(NativeMethods.RedrawWindow(Handles.IconList, 0, region, 0x145)); // INVALIDATE | ERASE | UPDATENOW | NOCHILDREN. Clear old glyphs with the transparent key, never repaint the whole desktop.
            RepaintCount++;
        }
        finally { NativeMethods.DeleteObject(region); }
    }
    private void OnIconsChanged()
    {
        try { IconPositionsChanged?.Invoke(); }
        catch (Exception error) { _logger.LogWarning(error, "Desktop notification interrupted"); }
    }
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
