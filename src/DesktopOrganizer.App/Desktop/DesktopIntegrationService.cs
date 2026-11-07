using DesktopOrganizer.Core.Localization;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Core.Storage;
using DesktopOrganizer.Infrastructure.Interop;
using DesktopOrganizer.App.ViewModels;
using System.ComponentModel;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.App.Desktop;

public sealed class DesktopIntegrationService : IDesktopIntegration, IDisposable
{
    private readonly Win32DesktopHost _host;
    private readonly FenceBitmapRenderer _renderer;
    private readonly ILogger<DesktopIntegrationService> _logger;
    private readonly DispatcherTimer _timer, _inputTimer;
    private readonly DispatcherTimer _hoverTimer;
    private readonly SettingsViewModel? _settings;
    private readonly FenceTransitionAnimator _animator = new();
    private (Guid Id, double Height)? _animatedHeight;
    private readonly Dictionary<Guid, FenceHoverState> _hoverStates = [];
    private TimeSpan? _scheduledHoverDeadline;
    private static TimeSpan HoverNow => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
    private bool HoverBusy => IsPresentingTransition || _finishing || _gestureOriginal is not null ||
        !_interactionAllowed || _host.HasActiveInteraction || _host.HasExplorerGesture || _host.IsLeftButtonDown ||
        _host.IsExplorerDragging || _host.IsNativeSelection || _icons is { ExplorerPhase: not ExplorerGesturePhase.Idle };
    private Fence DisplayFence(Fence fence) => fence.IsCollapsed && _hoverStates.TryGetValue(fence.Id, out var hover) && hover.Expanded
        ? fence.ToggleCollapsed() : fence;
    private Fence ManualFence(Fence fence) => _indices.TryGetValue(fence.Id, out var index) && fence.IsCollapsed != _fences[index].IsCollapsed
        ? fence.ToggleCollapsed() : fence;
    private Fence[] DisplayFences() => _fences.Select(DisplayFence).ToArray();
    private readonly DesktopIconCoordinator? _icons;
    private readonly Dictionary<Guid, (Fence Fence, FenceSurfaceLayout Layout, double Scale, FenceHit Hover)> _rendered = [];
    private readonly Dictionary<Guid, int> _indices = [];
    private readonly InteractionPerformance _performance = new();
    private Fence[] _fences = [];
    private Fence? _gestureOriginal, _preview;
    private double _gestureScale;
    private int _gestureIndex;
    private long _lastEditorUpdate;
    private bool _started, _disposed, _reconciling;
    private bool _interactionAllowed = true, _finishing;
    public bool IsPresentingTransition { get; private set; }
    private string? _lastError;
    public DesktopConnection Connection { get; private set; } = new(false, 0, "Desktop discovery pending");
    public event EventHandler? ConnectionChanged;
    public event EventHandler<FenceInteractionEventArgs>? Interaction;
    public event EventHandler<FenceStateChangeRequest>? StateChangeRequested;
    public void SetInteractionEnabled(bool enabled) { _interactionAllowed = enabled; _host.InteractionEnabled = enabled; if (_icons is not null) _icons.Enabled = enabled; }
    public Fence NormalizeFence(Fence original, Fence proposed) =>
        !proposed.Icons.IsEmpty && (original.Width != proposed.Width || original.Height != proposed.Height)
        ? FenceIconLayout.Arrange(proposed, _icons?.Metrics ?? IconGridMetrics.Default, _host.GetScale(proposed)) : proposed;

    public DesktopIntegrationService(Win32DesktopHost host, FenceBitmapRenderer renderer, ILogger<DesktopIntegrationService> logger,
        IDesktopIconService? icons = null, ILogger<DesktopIconCoordinator>? iconLogger = null, IIconRecoveryStore? recoveryStore = null,
        SettingsViewModel? settings = null)
    {
        _host = host; _renderer = renderer; _logger = logger;
        _settings = settings;
        if (_settings is not null) _settings.PropertyChanged += OnPreferencesChanged;
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Input);
        _hoverTimer.Tick += OnHoverDeadline;
        _host.HoverPointerMoved += OnHoverPointer;
        if (icons is not null)
        {
            _icons = new(icons, host, iconLogger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DesktopIconCoordinator>.Instance, RequestStateChange, recoveryStore);
            _icons.StatusChanged += OnIconStatusChanged;
            _icons.FramePresented += OnFramePresented;
            _icons.GestureSettled += RefreshHoverPointer;
        }
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += OnTick;
        _inputTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _inputTimer.Tick += OnInputTick;
        _host.Interaction += OnInteraction;
        _host.VisualChanged += OnVisualChanged;
        _host.CollapseRequested += OnCollapseRequested;
    }
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _started = true; Reconcile(); _timer.Start(); _inputTimer.Start(); _icons?.Start();
    }
    public void SetFences(IReadOnlyList<Fence> fences)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_gestureOriginal is not null) return; // Editor preview never drives native rendering back.
        _fences = fences.ToArray();
        foreach (var removed in _hoverStates.Keys.Except(_fences.Select(f => f.Id)).ToArray()) _hoverStates.Remove(removed);
        _indices.Clear();
        for (var i = 0; i < _fences.Length; i++) _indices[_fences[i].Id] = i;
        if (_started) Reconcile(discover: false);
        _icons?.SetFences(DisplayFences());
        RefreshHoverPointer();
    }
    private bool RequestStateChange(IReadOnlyList<Fence> fences)
    {
        // Coordinator works with displayed expansion; persistence retains manual collapse.
        var request = new FenceStateChangeRequest(fences.Select(f =>
            _hoverStates.TryGetValue(f.Id, out var hover) && hover.Expanded ? ManualFence(f) : f).ToArray());
        StateChangeRequested?.Invoke(this, request);
        return request.Accepted;
    }
    private void OnTick(object? sender, EventArgs args) { if (!_host.HasActiveInteraction && !_host.HasExplorerGesture && !_host.IsExplorerDragging && !_host.IsNativeSelection) Reconcile(); }
    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SettingsViewModel.AutoExpandOnHover)) RefreshHoverPointer();
    }
    private void RefreshHoverPointer()
    {
        if (_disposed || !_started) return;
        try { var pointer = _host.ReadHoverPointer(); OnHoverPointer(pointer.Point, pointer.OnDesktop); }
        catch (Exception error) { _logger.LogDebug(error, "Hover pointer unavailable"); }
    }
    private const double PrewarmMargin = 60, PrewarmApproach = 140;
    private void OnHoverPointer(DesktopPoint point, bool onDesktop)
    {
        if (_disposed || !_started) return;
        var enabled = _settings?.AutoExpandOnHover == true;
        var now = HoverNow;
        var busy = HoverBusy;
        foreach (var fence in _fences)
        {
            if (!_hoverStates.TryGetValue(fence.Id, out var hover))
            {
                if (!enabled) continue;
                _hoverStates.Add(fence.Id, hover = new());
            }
            if (!_rendered.TryGetValue(fence.Id, out var rendered)) continue;
            var displayed = DisplayFence(fence);
            var x = point.X - fence.X; var y = point.Y - fence.Y;
            var inside = onDesktop && x >= 0 && x < displayed.Width && y >= 0 &&
                y < FenceInteractionGeometry.VisibleHeight(displayed, rendered.Scale);
            var onHandle = inside && FenceInteractionGeometry.HitTest(displayed, x, y, rendered.Scale) != FenceHit.None;
            // Approaching a collapsed group: Explorer needs ~0.6 s to re-add hidden items, so they are returned
            // (still parked offscreen) before the hover expand, which then animates them with the Fence.
            if (enabled && onDesktop && displayed.IsCollapsed && !busy && _icons is not null &&
                x >= -PrewarmMargin * rendered.Scale && x < displayed.Width + PrewarmMargin * rendered.Scale &&
                y >= 0 && y < (FenceInteractionGeometry.HeaderHeight + PrewarmApproach) * rendered.Scale)
                _icons.PrewarmGroup(fence);
            hover.Observe(enabled, fence.IsCollapsed, inside, onHandle, busy, now);
        }
        ScheduleHoverDeadline();
    }
    private void ScheduleHoverDeadline()
    {
        var due = _hoverStates.Values.Where(h => h.DueAt is not null).Select(h => h.DueAt!.Value).ToArray();
        if (due.Length == 0) { _hoverTimer.Stop(); _scheduledHoverDeadline = null; return; }
        var deadline = due.Min();
        // Raw mouse movement must not continually requeue an already armed deadline.
        if (_hoverTimer.IsEnabled && _scheduledHoverDeadline == deadline) return;
        _hoverTimer.Stop(); _scheduledHoverDeadline = deadline;
        var delay = deadline - HoverNow;
        // Zero interval queues the one-shot callback directly on the Dispatcher,
        // without the minimum native timer delay or reentrancy in raw input handling.
        _hoverTimer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        _hoverTimer.Start();
    }
    private async void OnHoverDeadline(object? sender, EventArgs args)
    {
        _hoverTimer.Stop(); _scheduledHoverDeadline = null;
        RefreshHoverPointer(); // One deadline verification, not continuous pointer polling.
        if (_disposed || HoverBusy) return;
        var due = _hoverStates.FirstOrDefault(pair => pair.Value.Due(HoverNow) is not null);
        if (due.Value is null || !_indices.TryGetValue(due.Key, out var index)) return;
        _hoverTimer.Stop(); _scheduledHoverDeadline = null;
        var expanded = due.Value.Due(HoverNow)!.Value;
        var manual = _fences[index];
        var original = DisplayFence(manual);
        var next = expanded && manual.IsCollapsed ? manual.ToggleCollapsed() : manual;
        var previousLease = due.Value.Expanded;
        IsPresentingTransition = true; _host.InteractionEnabled = false;
        if (_icons is not null) _icons.InteractionActive = true;
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await FenceVisualTransaction.ExecuteAsync(original, next, async fence =>
            {
                await PresentFenceTransitionAsync(fence);
            }, async () =>
            {
                if (_icons is not null) await _icons.VerifyTransitionAsync(next);
                return !_disposed;
            });
            if (_disposed) return;
            due.Value.Presented(expanded);
            _icons?.SetFences(DisplayFences());
        }
        catch (Exception error)
        {
            due.Value.Presented(previousLease); due.Value.ManualAction();
            _logger.LogWarning(error, "Temporary Fence hover presentation failed; manual state retained");
        }
        finally
        {
            _animatedHeight = null;
            IsPresentingTransition = false;
            if (_icons is not null) _icons.InteractionActive = false;
            _host.InteractionEnabled = _interactionAllowed;
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
            Reconcile(discover: false); RefreshHoverPointer();
        }
    }
    private void OnIconStatusChanged(object? sender, EventArgs args) => Publish(Connection with { IconStatus = _icons?.Status ?? "" });
    private void OnVisualChanged(Guid id)
    {
        if (IsPresentingTransition || _finishing || _gestureOriginal is not null || !_indices.TryGetValue(id, out var index) || !_rendered.TryGetValue(id, out var state)) return;
        try { RenderFence(DisplayFence(_fences[index]), _host.GetArea(), state.Scale); }
        catch (Exception exception) { _logger.LogWarning(exception, "Desktop hover interrupted"); Reconcile(); }
    }
    private void OnInteraction(object? sender, FenceInteractionEventArgs args)
    {
        if (args.Phase == InteractionPhase.Started)
        {
            if (!_indices.TryGetValue(args.Fence.Id, out _gestureIndex)) return;
            _gestureOriginal = ManualFence(args.Fence); _preview = _gestureOriginal;
            if (_icons is not null) _icons.InteractionActive = true;
            _gestureScale = _rendered.TryGetValue(args.Fence.Id, out var state) ? state.Scale : _host.GetScale(args.Fence);
            _performance.Begin(args.Hit == FenceHit.Header ? "Drag" : "Resize", _host.RepaintCount, _host.BitmapPresentCount); _lastEditorUpdate = Stopwatch.GetTimestamp();
            Interaction?.Invoke(this, new(_gestureOriginal, args.Phase) { Hit = args.Hit });
            return;
        }
        if (_gestureOriginal is null) return;
        if (args.Phase == InteractionPhase.Preview)
        {
            var start = Stopwatch.GetTimestamp();
            _preview = ManualFence(args.Fence);
            _fences[_gestureIndex] = _preview; // Keep the temporary hover lease out of durable state.
            if (_icons is not null && !args.Fence.Icons.IsEmpty && !args.Fence.IsCollapsed)
                _icons.Preview(args.Fence, args.Hit != FenceHit.Header, _gestureScale,
                    () => RenderFence(args.Fence, _host.GetArea(), _gestureScale, live: true));
            else { RenderFence(args.Fence, _host.GetArea(), _gestureScale, live: true); _host.FlushDamage(); _performance.Record(start); }
            return;
        }
        FinishInteraction(args);
    }
    private void OnFramePresented(long requestedAt, double iconTime) => _performance.Record(requestedAt, iconTime);
    private async void FinishInteraction(FenceInteractionEventArgs args)
    {
        if (_finishing || _gestureOriginal is null) return;
        _finishing = true; _host.InteractionEnabled = false;
        try
        {
        var original = _gestureOriginal;
        var final = args.Phase == InteractionPhase.Cancelled ? original : _preview ?? args.Fence;
        if (args.Phase == InteractionPhase.Completed && !final.Icons.IsEmpty &&
            (original.Width != final.Width || original.Height != final.Height))
            final = FenceIconLayout.Arrange(final, _icons?.Metrics ?? IconGridMetrics.Default, _gestureScale);
        if (_icons is not null && !final.Icons.IsEmpty)
        {
            var displayedFinal = DisplayFence(final);
            _icons.Preview(displayedFinal, resize: false, _gestureScale,
                () => RenderFence(displayedFinal, _host.GetArea(), _gestureScale, live: true));
            await _icons.WaitForPresentationAsync();
        }
        if (_disposed) return;
        _gestureOriginal = null; _preview = null;
        _fences[_gestureIndex] = final;
        if (args.Phase == InteractionPhase.Completed) Interaction?.Invoke(this, new(final, InteractionPhase.Preview));
        Interaction?.Invoke(this, new(final, args.Phase));
        _icons?.ArmRecovery();
        _performance.Report(_logger, _host.RepaintCount, _host.BitmapPresentCount);
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Fence interaction completion failed");
            if (_gestureOriginal is not null)
            {
                var before = _gestureOriginal; _gestureOriginal = null; _preview = null;
                Interaction?.Invoke(this, new(before, InteractionPhase.Cancelled));
            }
        }
        finally
        {
            _finishing = false; if (_icons is not null) _icons.InteractionActive = false;
            _host.InteractionEnabled = _interactionAllowed;
            // A ViewModel save failure can replace the durable model with the original
            // while callbacks are gated. Restore its icons before resuming observation.
            if (!_disposed && _fences.Any(f => _rendered.TryGetValue(f.Id, out var state) && state.Fence != DisplayFence(f))) _icons?.SetFences(DisplayFences());
            Reconcile(discover: false);
            RefreshHoverPointer();
        }
    }
    private async void OnCollapseRequested(Fence original)
    {
        if (_disposed || !_interactionAllowed || IsPresentingTransition || _gestureOriginal is not null) return;
        if (!_indices.TryGetValue(original.Id, out var index)) return;
        var displayedOriginal = original;
        _hoverStates.TryGetValue(original.Id, out var hover);
        hover?.ManualAction();
        var temporaryExpanded = hover?.Expanded == true;
        var started = Stopwatch.GetTimestamp(); long visualCompletedAt = 0;
        IsPresentingTransition = true; _host.InteractionEnabled = false;
        if (_icons is not null) _icons.InteractionActive = true;
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
        var next = temporaryExpanded ? _fences[index] : original.ToggleCollapsed();
        var proposed = _fences.ToArray(); proposed[index] = next;
        hover?.Presented(false);
        try
        {
            await FenceVisualTransaction.ExecuteAsync(displayedOriginal, next, async fence =>
            {
                await PresentFenceTransitionAsync(fence);
                visualCompletedAt = Stopwatch.GetTimestamp();
            }, async () =>
            {
#if DEBUG
                _logger.LogInformation("{Action} visual latency: {Latency:F3}ms; icons={Count}; native positioning + damaged-region paint; persistence excluded",
                    next.IsCollapsed ? "Collapse hide" : "Expand restore", Stopwatch.GetElapsedTime(started, visualCompletedAt).TotalMilliseconds, next.Icons.Length);
#endif
                if (_icons is not null) await _icons.VerifyTransitionAsync(next);
                await Dispatcher.Yield(DispatcherPriority.Background); // Present first; disk writes are outside the visual critical path.
                return !_disposed && RequestStateChange(proposed);
            });
        }
        catch (Exception error) { hover?.Presented(temporaryExpanded); _logger.LogWarning(error, "Fence hide/show failed and original presentation was requested"); Publish(Connection with { Error = error.Message }); }
        finally
        {
            _animatedHeight = null;
            IsPresentingTransition = false; if (_icons is not null) _icons.InteractionActive = false;
            _host.InteractionEnabled = _interactionAllowed; ConnectionChanged?.Invoke(this, EventArgs.Empty);
            Reconcile(discover: false);
            RefreshHoverPointer();
        }
    }
    private async Task PresentFenceTransitionAsync(Fence target)
    {
        if (_disposed || _animator.IsDisposed) throw new OperationCanceledException("Desktop presentation is shutting down.");
        var scale = _host.GetScale(target);
        var endHeight = FenceInteractionGeometry.VisibleHeight(target, scale);
        var startHeight = _animatedHeight is { } current && current.Id == target.Id ? current.Height :
            _rendered.TryGetValue(target.Id, out var previous) ? FenceInteractionGeometry.VisibleHeight(previous.Fence, previous.Scale) : endHeight;
        var seconds = target.IsCollapsed ? _settings?.CollapseAnimationSeconds ?? 0.14 : _settings?.ExpandAnimationSeconds ?? 0.18;
        var area = _host.GetArea();
        var projection = new FenceHeightAnimationGeometry(target, area);
        Win32DesktopHost.HeightAnimation? nativeAnimation = null;
        var hover = _host.GetHover(target.Id);
        void Present(double height)
        {
            _animatedHeight = (target.Id, height);
            if (nativeAnimation is not null)
            {
                var layout = projection.At(height) ?? throw new InvalidOperationException("Animation left the desktop area.");
                nativeAnimation.Present(layout);
                _rendered[target.Id] = (target, layout, scale, hover);
            }
            else { RenderFence(target, area, scale, live: true, visibleHeight: height); _host.FlushDamage(); }
        }
        async Task PositionIconsAsync(Action present)
        {
            if (_icons is not null) await _icons.PresentTransitionAsync(target, present);
            else { present(); _host.FlushDamage(); }
        }
        if (seconds == 0 || Math.Abs(startHeight - endHeight) < 0.5)
            await PositionIconsAsync(() => Present(endHeight));
        else
        {
            var maximum = projection.At(Math.Max(startHeight, endHeight));
            if (maximum is null) throw new InvalidOperationException("No visible Fence animation geometry.");
            var presents = _host.BitmapPresentCount; var ulw = _host.LayeredUpdateMilliseconds;
            var repaints = _host.RepaintCount; var allocations = _host.BitmapAllocationCount;
            var rasterizations = _renderer.HeaderRasterizations;
            var regions = _host.HeightGeometryUpdates; var regionTime = _host.HeightRegionMilliseconds;
            using (nativeAnimation = _host.BeginHeightAnimation(target, maximum.Value, scale, _renderer.Render(target, maximum.Value, scale, hover)))
            {
                await using var iconAnimation = _icons is null ? null : await _icons.BeginAnimatedTransitionAsync(target,
                    startHeight <= FenceInteractionGeometry.HeaderHeight * scale + 1);
                var frameRepaints = _host.RepaintCount;
                var frameAllocations = _host.BitmapAllocationCount; var frameRasterizations = _renderer.HeaderRasterizations;
                if (iconAnimation is null) await _animator.AnimateAsync(startHeight, endHeight, seconds, Present);
                else await _animator.AnimateFramesAsync(startHeight, endHeight, seconds, frame =>
                {
                    var layout = projection.At(frame.Height) ?? throw new InvalidOperationException("Animation left the desktop area.");
                    var headerBottom = (int)Math.Ceiling(target.Y + FenceInteractionGeometry.HeaderHeight * scale);
                    var border = (int)Math.Ceiling(FenceInteractionGeometry.Border * scale);
                    var content = new DesktopBounds(layout.X + border, headerBottom, Math.Max(0, layout.Width - 2 * border),
                        Math.Max(0, layout.Y + layout.Height - border - headerBottom));
                    return iconAnimation.PresentAsync(frame, content, () => Present(frame.Height));
                });
#if DEBUG
                var metrics = _animator.LastMetrics;
                _logger.LogInformation("Fence animation {Direction}: clock=DWM presentation; duration={Duration:F2}s; frames={Frames}; fps={Fps:F1}; interval avg/max={Average:F2}/{Maximum:F2}ms; coalesced={Coalesced}; estimated missed cadence={Missed}; geometry updates={GeometryUpdates}; ULW={UlwCount}; ULW total={UlwTime:F3}ms; region total={RegionTime:F3}ms; explicit SetWindowPos=0 calls/0ms; Explorer redraws during frames={FrameRedraws}, whole transition={Redraws}; DIB allocations prepared/frame={PreparedAllocations}/{FrameAllocations}; WPF header rasterizations prepared/frame={PreparedRasterizations}/{FrameRasterizations}",
                    target.IsCollapsed ? "collapse" : "expand", seconds, metrics.Frames, metrics.Fps, metrics.AverageIntervalMs, metrics.MaximumIntervalMs,
                    metrics.CoalescedFrames, metrics.EstimatedMissedCadenceFrames, _host.HeightGeometryUpdates - regions, _host.BitmapPresentCount - presents,
                    _host.LayeredUpdateMilliseconds - ulw, _host.HeightRegionMilliseconds - regionTime, _host.RepaintCount - frameRepaints,
                    _host.RepaintCount - repaints, frameAllocations - allocations, _host.BitmapAllocationCount - frameAllocations,
                    frameRasterizations - rasterizations, _renderer.HeaderRasterizations - frameRasterizations);
#endif
#if DEBUG
                if (iconAnimation is not null) _logger.LogInformation("Coupled icon animation performance: icons={Count}; bulk updates={Batches}; targeted redraws={Redraws}; frame commit total={Milliseconds:F3}ms; Shell/presentation={Shell:F3}ms; targeted paint={Paint:F3}ms; Shell finish={Finish:F3}ms at progress={Progress:F3}; stagger=0; one shared DWM progress; Explorer region unchanged",
                    target.Icons.Length, iconAnimation.BulkUpdates, iconAnimation.Redraws, iconAnimation.PositionMilliseconds,
                    iconAnimation.ShellMilliseconds, iconAnimation.RedrawMilliseconds, iconAnimation.ShellFinishMilliseconds, iconAnimation.IconCommitProgress);
#endif
            }
        }
        _animatedHeight = null;
    }
    private void OnInputTick(object? sender, EventArgs args)
    {
        try
        {
            _host.PollActiveInteraction();
            if (_preview is not null && Stopwatch.GetElapsedTime(_lastEditorUpdate).TotalMilliseconds >= 100 &&
                Application.Current?.MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized })
            {
                _lastEditorUpdate = Stopwatch.GetTimestamp();
                Interaction?.Invoke(this, new(_preview, InteractionPhase.Preview));
            }
        }
        catch (Exception exception) { _logger.LogWarning(exception, "Desktop input interrupted"); Reconcile(); }
    }
    public void ShowDesktop()
    {
        try { DesktopShell.ShowDesktop(); }
        catch (Exception exception) { _logger.LogError(exception, "Show Desktop failed"); Publish(Connection with { Error = exception.Message }); }
    }
    private void RenderFence(Fence fence, DesktopArea area, double scale, bool live = false, double? visibleHeight = null)
    {
        // Intermediate height belongs to presentation only; model Height remains the normal height.
        var geometry = visibleHeight is { } height
            ? (fence.IsCollapsed ? fence.ToggleCollapsed() : fence).WithBounds(fence.X, fence.Y, fence.Width, height)
            : fence;
        var layout = FenceSurfaceLayout.Clip(geometry, area, scale);
        if (layout is null) return;
        var hover = _host.GetHover(fence.Id);
        var state = (fence, layout.Value, scale, hover);
        var exists = _rendered.TryGetValue(fence.Id, out var previous);
        if (exists && previous == state) return;
        if (exists && previous.Fence.Name == fence.Name && previous.Fence.X == fence.X && previous.Fence.Y == fence.Y &&
            previous.Fence.Appearance == fence.Appearance &&
            previous.Fence.Width == fence.Width && previous.Fence.Height == fence.Height && previous.Fence.IsCollapsed == fence.IsCollapsed &&
            previous.Layout == layout.Value && previous.Scale == scale && previous.Hover == hover && _host.Contains(fence.Id))
        {
            _host.SyncFenceModel(fence); _rendered[fence.Id] = state; return;
        }
        var repaint = !exists || (!live && !_host.Contains(fence.Id)) ||
            previous.Fence.Name != fence.Name || previous.Fence.IsCollapsed != fence.IsCollapsed ||
            previous.Fence.Appearance != fence.Appearance ||
            previous.Fence.Width != fence.Width || previous.Fence.X - previous.Layout.X != fence.X - layout.Value.X ||
            previous.Fence.Y - previous.Layout.Y != fence.Y - layout.Value.Y ||
            previous.Layout.Width != layout.Value.Width || previous.Scale != scale || previous.Hover != hover;
        _host.Update(fence, layout.Value, scale, repaint ? _renderer.Render(fence, layout.Value, scale, hover) : null);
        _rendered[fence.Id] = state;
    }
    private void Reconcile(bool discover = true)
    {
        if (_reconciling || _disposed || IsPresentingTransition || _finishing || _gestureOriginal is not null) return;
        _reconciling = true;
        try
        {
            if ((discover && !_host.Connect()) || _host.Handles is null)
            {
                _rendered.Clear(); _renderer.Clear();
                Publish(new(false, 0, _host.Diagnostics, "No validated desktop host; retrying", _icons?.Status ?? "")); return;
            }
            var area = _host.GetArea();
            var visible = new HashSet<Guid>();
            foreach (var fence in _fences)
            {
                var scale = _host.GetScale(fence);
                if (FenceSurfaceLayout.Clip(fence, area, scale) is null) continue;
                visible.Add(fence.Id);
                if (!_host.Contains(fence.Id)) _rendered.Remove(fence.Id);
                RenderFence(DisplayFence(fence), area, scale);
            }
            foreach (var removed in _rendered.Keys.Except(visible).ToArray())
            {
                _host.Remove(removed); _rendered.Remove(removed); _renderer.Remove(removed);
            }
            _lastError = null;
            _host.FlushDamage();
            Publish(new(true, _host.SurfaceCount, _host.Handles!.ToString(), IconStatus: _icons?.Status ?? ""));
        }
        catch (Exception exception)
        {
            if (_lastError != exception.Message) _logger.LogError(exception, "Desktop surfaces unavailable; retrying");
            _lastError = exception.Message;
            try { _host.Clear(); } catch (Exception cleanupError) { _logger.LogWarning(cleanupError, "Desktop cleanup failed"); }
            _rendered.Clear(); _renderer.Clear();
            Publish(new(false, 0, _host.Diagnostics, exception.Message, _icons?.Status ?? ""));
        }
        finally { _reconciling = false; }
    }
    private void Publish(DesktopConnection state)
    {
        if (state == Connection) return;
        Connection = state;
        _logger.LogInformation("Desktop integration: {State}; Surfaces={Count}; {Diagnostics}; {Icons}; Error={Error}",
            state.Connected ? "Connected" : "Disconnected", state.SurfaceCount, state.Diagnostics, state.IconStatus, state.Error);
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _animator.Dispose();
        _timer.Stop(); _timer.Tick -= OnTick; _inputTimer.Stop(); _inputTimer.Tick -= OnInputTick;
        _hoverTimer.Stop(); _hoverTimer.Tick -= OnHoverDeadline;
        _host.HoverPointerMoved -= OnHoverPointer;
        if (_settings is not null) _settings.PropertyChanged -= OnPreferencesChanged;
        _icons?.Dispose();
        _reconciling = true; _host.Dispose();
        _host.Interaction -= OnInteraction; _host.VisualChanged -= OnVisualChanged;
        _host.CollapseRequested -= OnCollapseRequested;
        if (_icons is not null) { _icons.StatusChanged -= OnIconStatusChanged; _icons.FramePresented -= OnFramePresented; _icons.GestureSettled -= RefreshHoverPointer; }
        _disposed = true; _rendered.Clear(); _renderer.Clear();
    }
    public async Task PrepareExitAsync()
    {
        var started = Stopwatch.GetTimestamp();
        var wasAllowed = _interactionAllowed;
        // Block new Fence gestures, but let an already released Explorer drop finish and persist.
        _interactionAllowed = false; _host.InteractionEnabled = false;
        try
        {
            while (_gestureOriginal is not null || _finishing || IsPresentingTransition)
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(10))
                    throw new InvalidOperationException(Texts.T("Дождитесь завершения изменения Fence и повторите выход."));
                await Task.Delay(20);
            }
            // ViewModel has already persisted every completed gesture and accepted command.
            // Do not rewrite unreadable/corrupt storage merely because the process is exiting.
            if (_icons is not null) await _icons.PrepareExitAsync();
        }
        catch { SetInteractionEnabled(wasAllowed); throw; }
    }
}
