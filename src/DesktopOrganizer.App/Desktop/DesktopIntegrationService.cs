using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Core.Storage;
using DesktopOrganizer.Infrastructure.Interop;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.App.Desktop;

public sealed class DesktopIntegrationService : IDesktopIntegration, IDisposable
{
    private readonly Win32DesktopHost _host;
    private readonly FenceBitmapRenderer _renderer;
    private readonly ILogger<DesktopIntegrationService> _logger;
    private readonly DispatcherTimer _timer, _inputTimer;
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
        IDesktopIconService? icons = null, ILogger<DesktopIconCoordinator>? iconLogger = null, IIconRecoveryStore? recoveryStore = null)
    {
        _host = host; _renderer = renderer; _logger = logger;
        if (icons is not null)
        {
            _icons = new(icons, host, iconLogger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DesktopIconCoordinator>.Instance, RequestStateChange, recoveryStore);
            _icons.StatusChanged += OnIconStatusChanged;
            _icons.FramePresented += OnFramePresented;
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
        _indices.Clear();
        for (var i = 0; i < _fences.Length; i++) _indices[_fences[i].Id] = i;
        if (_started) Reconcile(discover: false);
        _icons?.SetFences(_fences);
    }
    private bool RequestStateChange(IReadOnlyList<Fence> fences)
    {
        var request = new FenceStateChangeRequest(fences);
        StateChangeRequested?.Invoke(this, request);
        return request.Accepted;
    }
    private void OnTick(object? sender, EventArgs args) { if (!_host.HasActiveInteraction && !_host.HasExplorerGesture && !_host.IsExplorerDragging && !_host.IsNativeSelection) Reconcile(); }
    private void OnIconStatusChanged(object? sender, EventArgs args) => Publish(Connection with { IconStatus = _icons?.Status ?? "" });
    private void OnVisualChanged(Guid id)
    {
        if (IsPresentingTransition || _finishing || _gestureOriginal is not null || !_indices.TryGetValue(id, out var index) || !_rendered.TryGetValue(id, out var state)) return;
        try { RenderFence(_fences[index], _host.GetArea(), state.Scale); }
        catch (Exception exception) { _logger.LogWarning(exception, "Desktop hover interrupted"); Reconcile(); }
    }
    private void OnInteraction(object? sender, FenceInteractionEventArgs args)
    {
        if (args.Phase == InteractionPhase.Started)
        {
            if (!_indices.TryGetValue(args.Fence.Id, out _gestureIndex)) return;
            _gestureOriginal = args.Fence; _preview = args.Fence;
            if (_icons is not null) _icons.InteractionActive = true;
            _gestureScale = _rendered.TryGetValue(args.Fence.Id, out var state) ? state.Scale : _host.GetScale(args.Fence);
            _performance.Begin(args.Hit == FenceHit.Header ? "Drag" : "Resize", _host.RepaintCount, _host.BitmapPresentCount); _lastEditorUpdate = Stopwatch.GetTimestamp();
            Interaction?.Invoke(this, args);
            return;
        }
        if (_gestureOriginal is null) return;
        if (args.Phase == InteractionPhase.Preview)
        {
            var start = Stopwatch.GetTimestamp();
            _preview = args.Fence;
            _fences[_gestureIndex] = args.Fence; // One immutable model replacement, no UI collection feedback.
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
            _icons.Preview(final, resize: false, _gestureScale,
                () => RenderFence(final, _host.GetArea(), _gestureScale, live: true));
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
            if (!_disposed && _fences.Any(f => _rendered.TryGetValue(f.Id, out var state) && state.Fence != f)) _icons?.SetFences(_fences);
            Reconcile(discover: false);
        }
    }
    private async void OnCollapseRequested(Fence original)
    {
        if (_disposed || !_interactionAllowed || IsPresentingTransition || _gestureOriginal is not null) return;
        if (!_indices.TryGetValue(original.Id, out var index)) return;
        var started = Stopwatch.GetTimestamp(); long visualCompletedAt = 0;
        IsPresentingTransition = true; _host.InteractionEnabled = false;
        if (_icons is not null) _icons.InteractionActive = true;
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
        var next = original.ToggleCollapsed(); var proposed = _fences.ToArray(); proposed[index] = next;
        try
        {
            await FenceVisualTransaction.ExecuteAsync(original, next, async fence =>
            {
                if (_icons is not null)
                {
                    await _icons.PresentTransitionAsync(fence, () => RenderFence(fence, _host.GetArea(), _host.GetScale(fence), live: true));
                    visualCompletedAt = _icons.LastPresentationAt;
                }
                else { RenderFence(fence, _host.GetArea(), _host.GetScale(fence), live: true); _host.FlushDamage(); visualCompletedAt = Stopwatch.GetTimestamp(); }
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
        catch (Exception error) { _logger.LogWarning(error, "Fence hide/show failed and original presentation was requested"); Publish(Connection with { Error = error.Message }); }
        finally
        {
            IsPresentingTransition = false; if (_icons is not null) _icons.InteractionActive = false;
            _host.InteractionEnabled = _interactionAllowed; ConnectionChanged?.Invoke(this, EventArgs.Empty);
            Reconcile(discover: false);
        }
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
    private void RenderFence(Fence fence, DesktopArea area, double scale, bool live = false)
    {
        var layout = FenceSurfaceLayout.Clip(fence, area, scale);
        if (layout is null) return;
        var hover = _host.GetHover(fence.Id);
        var state = (fence, layout.Value, scale, hover);
        var exists = _rendered.TryGetValue(fence.Id, out var previous);
        if (exists && previous == state) return;
        if (exists && previous.Fence.Name == fence.Name && previous.Fence.X == fence.X && previous.Fence.Y == fence.Y &&
            previous.Fence.Width == fence.Width && previous.Fence.Height == fence.Height && previous.Fence.IsCollapsed == fence.IsCollapsed &&
            previous.Layout == layout.Value && previous.Scale == scale && previous.Hover == hover && _host.Contains(fence.Id))
        {
            _host.SyncFenceModel(fence); _rendered[fence.Id] = state; return;
        }
        var repaint = !exists || (!live && !_host.Contains(fence.Id)) ||
            previous.Fence.Name != fence.Name || previous.Fence.IsCollapsed != fence.IsCollapsed ||
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
                RenderFence(fence, area, scale);
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
        _timer.Stop(); _timer.Tick -= OnTick; _inputTimer.Stop(); _inputTimer.Tick -= OnInputTick;
        _icons?.Dispose();
        _reconciling = true; _host.Dispose();
        _host.Interaction -= OnInteraction; _host.VisualChanged -= OnVisualChanged;
        _host.CollapseRequested -= OnCollapseRequested;
        if (_icons is not null) { _icons.StatusChanged -= OnIconStatusChanged; _icons.FramePresented -= OnFramePresented; }
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
                    throw new InvalidOperationException("Дождитесь завершения изменения Fence и повторите выход.");
                await Task.Delay(20);
            }
            // ViewModel has already persisted every completed gesture and accepted command.
            // Do not rewrite unreadable/corrupt storage merely because the process is exiting.
            if (_icons is not null) await _icons.PrepareExitAsync();
        }
        catch { SetInteractionEnabled(wasAllowed); throw; }
    }
}
