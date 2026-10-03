using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Core.Storage;
using DesktopOrganizer.Infrastructure.Interop;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.App.Desktop;

/// <summary>Event-driven drop reconciliation and serialized/coalesced live positioning.</summary>
public sealed class DesktopIconCoordinator : IDisposable
{
    private readonly IDesktopIconService _icons;
    private readonly IconLayoutTransaction _transaction;
    private readonly Win32DesktopHost _host;
    private readonly ILogger<DesktopIconCoordinator> _logger;
    private readonly DispatcherTimer _timer, _releaseTimer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<IReadOnlyList<Fence>, bool> _commit;
    private readonly IIconRecoveryStore? _recoveryStore;
    private IReadOnlyList<DesktopIconTarget> _recovery = [];
    private IReadOnlyList<Fence> _fences = [];
    private Dictionary<string, DesktopIcon> _matched = new(FenceIconLayout.IdentityComparer);
    private IReadOnlyList<DesktopArea> _monitors = [];
    private MoveBatch? _pending;
    private bool _reading, _moving, _initialized, _disposed, _applyingDrop, _positioningAvailable, _requested, _scheduled, _recoveryLoaded;
    private long _revision;
    private readonly ExplorerGesture _explorerGesture = new();
    private Task? _selectionRead;
    private bool _layoutDeferred;
    private bool _stopping;
    public ExplorerGesturePhase ExplorerPhase => _explorerGesture.Phase;
    private TaskCompletionSource? _idle;
    public event Action<long, double>? FramePresented;
    public long LastPresentationAt { get; private set; }
    public bool InteractionActive { get; set; }
    private string? _lastMoveError;
    public bool Enabled { get; set; } = true;
    public IconGridMetrics Metrics { get; private set; } = IconGridMetrics.Default;
    public string Status { get; private set; } = "Desktop icons: discovery pending";
    public event EventHandler? StatusChanged;
    public DesktopIconCoordinator(IDesktopIconService icons, Win32DesktopHost host, ILogger<DesktopIconCoordinator> logger,
        Func<IReadOnlyList<Fence>, bool> commit, IIconRecoveryStore? recoveryStore = null)
    {
        _icons = icons; _transaction = new(icons); _host = host; _logger = logger; _commit = commit; _recoveryStore = recoveryStore;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _releaseTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick; _releaseTimer.Tick += OnReleaseTick;
        _host.IconPositionsChanged += OnExplorerChanged;
        _host.ExplorerPointer += OnExplorerPointer;
        _host.SetSelectionReader(icons as IDesktopSelectionReader);
    }
    public void Start() { _timer.Start(); RequestObservation(); }
    public void SetFences(IReadOnlyList<Fence> fences)
    {
        _fences = fences; _revision++;
        if (_initialized && _positioningAvailable && !_applyingDrop && !InteractionActive && !_host.HasActiveInteraction)
        {
            if (ExplorerBusy()) _layoutDeferred = true;
            else QueueAll();
        }
    }
    private void OnExplorerPointer(ExplorerPointerEvent input)
    {
        if (_disposed || _stopping) return;
        if (input.Phase == ExplorerPointerPhase.Pressed)
        {
            if (input.SelectionSnapshot is null) _host.Trace.Begin();
            _explorerGesture.Begin(input.Point, input.Background); _selectionRead = null;
            if (input.SelectionSnapshot is not null)
                _selectionRead = CaptureSelectionAsync(input.SelectionSnapshot, _explorerGesture.Sequence);
            else if (!input.Background && _icons is IDesktopSelectionReader reader)
                _selectionRead = CaptureSelectionAsync(reader.ReadSelectionAsync(input.Point, _shutdown.Token), _explorerGesture.Sequence);
        }
        else if (input.Phase == ExplorerPointerPhase.DragStarted && _explorerGesture.StartDrag())
        {
            _host.Trace.Mark("DragStarted");
            if (_selectionRead is null && _icons is IDesktopSelectionReader reader)
                _selectionRead = CaptureSelectionAsync(reader.ReadSelectionAsync(_explorerGesture.PressPoint, _shutdown.Token), _explorerGesture.Sequence);
        }
        else if (input.Phase == ExplorerPointerPhase.Released)
        {
            _host.Trace.Mark("Gesture.release");
            _explorerGesture.Release(input.Point, input.DesktopTarget); _releaseTimer.Start(); RequestObservation();
        }
        else if (input.Phase == ExplorerPointerPhase.Cancelled)
        {
            _explorerGesture.Cancel(); _selectionRead = null; _releaseTimer.Start();
        }
    }
    private async Task CaptureSelectionAsync(Task<DesktopSelection> snapshot, long sequence)
    {
        try { _explorerGesture.Capture(sequence, await snapshot); }
        catch (OperationCanceledException) { }
        catch (Exception error) { _logger.LogWarning(error, "Could not capture Explorer selection; this gesture will not reassign partial groups"); }
    }
    private void OnExplorerChanged()
    {
        if (_disposed || _moving || _applyingDrop || InteractionActive || _host.HasActiveInteraction) return;
        if (ExplorerBusy()) { _releaseTimer.Start(); return; }
        RequestObservation();
    }
    private void OnReleaseTick(object? sender, EventArgs args)
    {
        if (ExplorerBusy()) return;
        _releaseTimer.Stop(); RequestObservation();
    }
    private void RequestObservation()
    {
        if (_disposed || _stopping) return;
        _requested = true;
        if (_reading || _moving || _scheduled) return;
        _scheduled = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { _scheduled = false; OnTick(this, EventArgs.Empty); }));
    }
    private async void OnTick(object? sender, EventArgs args)
    {
        if (_disposed || _stopping || _reading || _moving || InteractionActive || _host.HasActiveInteraction) return;
        if (ExplorerBusy()) { _releaseTimer.Start(); return; }
        _reading = true; _requested = false;
        try
        {
            if (_selectionRead is not null) await _selectionRead;
            if (Busy()) return;
            _host.FinishNativeSelection();
            if (_initialized && _positioningAvailable && Enabled && _host.PresentationConnected &&
                _explorerGesture.Drop is { DesktopTarget: true } drop && DropAffectsFence(drop))
            { await ReconcileDropAsync(drop); return; }
            var snapshot = await _icons.ReadAsync(_shutdown.Token);
            if (Busy()) return;
            SetStatus($"Desktop icons: {(snapshot.Connected ? snapshot.Icons.Count.ToString() : "Disconnected")}" +
                (snapshot.Error is null ? snapshot.Notice is null ? "" : " · " + snapshot.Notice : " · " + snapshot.Error));
            if (!snapshot.Connected) { _initialized = false; _positioningAvailable = false; return; }
            Metrics = snapshot.Metrics; _host.IconMetrics = Metrics;
            _matched = FenceIconLayout.Match(snapshot.Icons);
            _host.SetIcons(snapshot.Icons);
            var available = Enabled && _host.PresentationConnected && snapshot.CanPosition && snapshot.Error is null;
            if (!_initialized || available && !_positioningAvailable)
            {
                _initialized = true; _positioningAvailable = available;
                if (available) { _monitors = _host.GetMonitorAreas(); QueueAll(); }
                return;
            }
            _positioningAvailable = available;
            // Polling observes Explorer, but must never infer a partial drop from individual bounds.
            ApplySnapshot(snapshot); CompleteGesture();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _host.Trace.Mark("Reconciliation.failure");
            _logger.LogWarning(exception, "Desktop icon reconciliation failed; durable assignment was not advanced");
            SetStatus("Desktop icons: " + exception.Message); _initialized = false;
            if (_explorerGesture.Phase == ExplorerGesturePhase.PostDropReconciliation) CompleteGesture();
        }
        finally
        {
            _reading = false;
            if (_pending is not null && !_moving && !_disposed) MoveLatest();
            else if (_requested) RequestObservation();
        }
    }
    private bool DropAffectsFence(ExplorerDrop drop) => FenceIconLayout.DropTarget(_fences, drop.Point, _host.GetScale) is not null ||
        _fences.SelectMany(f => f.Icons).Any(i => drop.DraggedIds.Contains(i.StableId, FenceIconLayout.IdentityComparer));
    private async Task ReconcileDropAsync(ExplorerDrop drop)
    {
        var revision = _revision; var sequence = _explorerGesture.Sequence;
        var source = _fences.ToArray(); var monitors = _monitors.ToArray();
        var scales = source.ToDictionary(f => f.Id, _host.GetScale);
        IReadOnlyList<Fence> changed = source;
        IReadOnlyList<DesktopIconTarget> targets = [], recovery = [];
        var beforeCounters = (_icons as IDesktopPositionDiagnostics)?.PositionCounters ?? default;
        var beforePaints = _host.RepaintCount;
        _applyingDrop = true;
        try
        {
            using var paint = _host.SuppressExplorerPainting();
            var positioned = await _transaction.ApplyPlannedAsync(
                snapshot =>
                {
                    // Runs on the Shell worker before the redraw lease. Immutable inputs and pure
                    // Core geometry only; no UI, host calls, recovery files or persistence here.
                    changed = FenceIconLayout.ApplyGroupDrop(source, drop, snapshot.Icons, snapshot.Metrics, f => scales[f.Id]);
                    targets = FenceIconLayout.ChangedTargets(IconPlacement.Targets(changed, monitors, snapshot.Metrics), snapshot.Icons);
                    _host.Trace.Mark("Assignment/layout.proposal", drop.DraggedIds.Count);
                    return targets;
                },
                async verified =>
                {
                    // Shell redraw has completed before any durable work or model feedback.
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    if (Busy() || _explorerGesture.Sequence != sequence || _revision != revision) return false;
                    Metrics = verified.Metrics; _host.IconMetrics = Metrics;
                    recovery = IconPlacement.ReturnPositions(changed); PrepareRecovery(recovery);
                    _host.Trace.Mark("Persistence.begin");
                    var accepted = SameState(_fences, changed) || _commit(changed);
                    _host.Trace.Mark("Persistence.end"); return accepted;
                }, _shutdown.Token);
            ApplySnapshot(positioned); CompleteRecovery(recovery);
            var counters = (_icons as IDesktopPositionDiagnostics)?.PositionCounters ?? default;
            var target = FenceIconLayout.DropTarget(changed, drop.Point, f => scales[f.Id]);
            _logger.LogInformation("Desktop drop reconciled: SelectedCount={Selected}; DraggedItemIdHash={Item}; TargetFence={Fence}; AssignedAfterDrop={Assigned}; MovedTargets={Targets}; BulkCalls={Batches}; ShellRedrawCompletions={Redraw}; ExtraRepaints={Paints}",
                drop.DraggedIds.Count, IdentityHash(drop.DraggedItemId), target?.Id,
                changed.SelectMany(f => f.Icons).Count(i => drop.DraggedIds.Contains(i.StableId, FenceIconLayout.IdentityComparer)),
                targets.Count, counters.Batches - beforeCounters.Batches, counters.RedrawCompletions - beforeCounters.RedrawCompletions, _host.RepaintCount - beforePaints);
            if (_explorerGesture.Sequence == sequence) CompleteGesture();
        }
        finally { _applyingDrop = false; }
    }
    private bool ExplorerBusy() => _explorerGesture.InProgress || _host.HasExplorerGesture || _host.IsLeftButtonDown || _host.IsExplorerDragging;
    private bool ExplorerPositioningBusy() => _host.HasExplorerGesture ||
        _explorerGesture.BlocksFencePresentation(InteractionActive || _host.HasActiveInteraction, _host.IsExplorerDragging);
    private bool Busy() => _disposed || InteractionActive || _host.HasActiveInteraction || ExplorerBusy();
    private void CompleteGesture()
    {
        _host.Trace.Complete(_explorerGesture.Drop is null ? "selection/click/cancel" : "drop");
        _explorerGesture.Reset(); _selectionRead = null;
        if (_layoutDeferred) { _layoutDeferred = false; QueueAll(); }
    }
    private static string IdentityHash(string identity) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())))[..12];
    private void ApplySnapshot(DesktopIconSnapshot snapshot)
    {
        _matched = FenceIconLayout.Match(snapshot.Icons);
        _host.SetIcons(snapshot.Icons); _host.EnsureStacking();
    }
    public void Preview(Fence fence, bool resize, double scale, Action? present = null)
    {
        if (_disposed) return;
        if (!_initialized || !Enabled || !_positioningAvailable || fence.Icons.IsEmpty)
        { present?.Invoke(); _host.FlushDamage(); return; }
        if (fence.IsCollapsed) { present?.Invoke(); _host.FlushDamage(); return; }
        var batch = new MoveBatch(fence.Icons.Length) { Present = present };
        for (var i = 0; i < fence.Icons.Length; i++)
        {
            var assignment = fence.Icons[i];
            if (!_matched.ContainsKey(assignment.StableId)) continue;
            var offset = resize ? FenceIconLayout.GridOffset(i, fence.Width, Metrics, scale) : new DesktopPoint(assignment.OffsetX, assignment.OffsetY);
            batch.Add(new(assignment.StableId, new(fence.X + offset.X, fence.Y + offset.Y)));
        }
        Enqueue(batch);
    }
    public Task WaitForPresentationAsync() => _idle?.Task ?? Task.CompletedTask;
    public async Task PresentTransitionAsync(Fence fence, Action present)
    {
        if (fence.Icons.IsEmpty) { present(); _host.FlushDamage(); LastPresentationAt = Stopwatch.GetTimestamp(); return; }
        if (!_initialized || !_positioningAvailable) throw new InvalidOperationException("Explorer icon positioning is not ready.");
        await WaitForPresentationAsync();
        // Return positions are armed during startup/assignment/end-of-gesture, never written on the collapse visual path.
        if (fence.IsCollapsed && fence.Icons.Any(i => !_recovery.Any(r => FenceIconLayout.IdentityComparer.Equals(r.StableId, i.StableId))))
            throw new InvalidOperationException("Icon recovery must be armed before collapse.");
        var proposed = _fences.Select(f => f.Id == fence.Id ? fence : f).ToArray();
        var targets = IconPlacement.Targets(proposed, _monitors, Metrics).Where(t => _matched.ContainsKey(t.StableId)).ToArray();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batch = new MoveBatch(Math.Max(1, targets.Length)) { Present = present, Completion = completion };
        foreach (var target in targets) batch.Add(target);
        Enqueue(batch); await completion.Task;
    }
    public async Task VerifyTransitionAsync(Fence fence)
    {
        var proposed = _fences.Select(f => f.Id == fence.Id ? fence : f).ToArray();
        var targets = IconPlacement.Targets(proposed, _monitors, Metrics).Where(t => _matched.ContainsKey(t.StableId)).ToArray();
        if (!IconPlacement.AtTargets(await _icons.ReadAsync(_shutdown.Token), targets))
            throw new InvalidOperationException("Explorer did not retain the hide/show positions.");
    }
    public void ArmRecovery() => WriteRecovery(IconPlacement.ReturnPositions(_fences));
    private void QueueAll()
    {
        if (!Enabled || !_positioningAvailable || _disposed) return;
        if (ExplorerPositioningBusy()) { _layoutDeferred = true; return; }
        try
        {
            LoadRecovery(); _monitors = _host.GetMonitorAreas();
            var targets = IconPlacement.Targets(_fences, _monitors, Metrics);
            var ids = targets.Select(t => t.StableId).ToHashSet(FenceIconLayout.IdentityComparer);
            var orphaned = _recovery.Where(t => !ids.Contains(t.StableId)).ToArray();
            var batch = new MoveBatch(Math.Max(1, targets.Count + orphaned.Length)) { Recovery = IconPlacement.ReturnPositions(_fences), Verified = true };
            foreach (var target in FenceIconLayout.ChangedTargets(targets.Concat(orphaned).ToArray(), _matched.Values.ToArray())) batch.Add(target);
            Enqueue(batch);
        }
        catch (Exception error) { _logger.LogWarning(error, "Could not prepare desktop icon restoration"); SetStatus("Desktop icons: " + error.Message); }
    }
    private void LoadRecovery()
    {
        if (_recoveryLoaded) return;
        _recovery = _recoveryStore?.Load() ?? []; _recoveryLoaded = true;
    }
    private void PrepareRecovery(IReadOnlyList<DesktopIconTarget> next)
    {
        LoadRecovery();
        if (next.Count != 0 && _recoveryStore is null) throw new InvalidOperationException("A recovery journal is required before parking real icons.");
        var merged = _recovery.Concat(next).GroupBy(t => t.StableId, FenceIconLayout.IdentityComparer).Select(g => g.Last()).ToArray();
        WriteRecovery(merged);
    }
    private void CompleteRecovery(IReadOnlyList<DesktopIconTarget> next) => WriteRecovery(next);
    private void WriteRecovery(IReadOnlyList<DesktopIconTarget> next)
    {
        if (_recovery.SequenceEqual(next)) return;
        _recoveryStore?.Save(next); _recovery = next;
    }
    private void Enqueue(MoveBatch batch)
    {
        if (_disposed) { batch.Completion?.TrySetCanceled(); batch.Dispose(); return; }
        if (batch.Count == 0 && !batch.Verified) { batch.Present?.Invoke(); _host.FlushDamage(); LastPresentationAt = Stopwatch.GetTimestamp(); batch.Completion?.TrySetResult(); batch.Dispose(); return; }
        if (_idle is null || _idle.Task.IsCompleted) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending?.Completion?.TrySetCanceled(); _pending?.Dispose(); _pending = batch;
        if (!_moving && !_reading) MoveLatest();
    }
    private async void MoveLatest()
    {
        _moving = true;
        try
        {
            while (_pending is not null && !_disposed)
            {
                if (ExplorerPositioningBusy()) { _releaseTimer.Start(); return; }
                var batch = _pending; _pending = null;
                try
                {
                    _host.SetIconTargets(batch);
                    var positionStart = Stopwatch.GetTimestamp();
                    if (batch.Verified)
                    {
                        PrepareRecovery(batch.Recovery!);
                        ApplySnapshot(await _transaction.PositionVerifiedAsync(batch, _shutdown.Token));
                        CompleteRecovery(batch.Recovery!);
                    }
                    else
                    {
                        await _icons.PositionAsync(batch, _shutdown.Token);
                        if (_disposed) return;
                        foreach (var target in batch)
                        {
                            if (!_matched.TryGetValue(target.StableId, out var old)) continue;
                            var deltaX = target.Position.X - old.Position.X; var deltaY = target.Position.Y - old.Position.Y;
                            var updated = old with { Position = target.Position, Bounds = old.Bounds with { X = old.Bounds.X + deltaX, Y = old.Bounds.Y + deltaY } };
                            _matched[target.StableId] = updated;
                        }
                        _host.SetIcons(_matched.Values.ToArray()); _host.EnsureStacking();
                    }
                    var iconTime = Stopwatch.GetElapsedTime(positionStart).TotalMilliseconds;
                    batch.Present?.Invoke(); _host.FlushDamage();
                    LastPresentationAt = Stopwatch.GetTimestamp();
                    if (batch.Present is not null) FramePresented?.Invoke(batch.RequestedAt, iconTime);
                    batch.Completion?.TrySetResult();
                    _lastMoveError = null;
                }
                catch (Exception error) { batch.Completion?.TrySetException(error); throw; }
                finally { batch.Dispose(); }
            }
        }
        catch (OperationCanceledException error) { _idle?.TrySetCanceled(error.CancellationToken); }
        catch (Exception exception)
        {
            if (_lastMoveError != exception.Message) _logger.LogWarning(exception, "Could not position desktop icons; recovery journal retained");
            _lastMoveError = exception.Message; SetStatus("Desktop icons: " + exception.Message);
            _idle?.TrySetException(exception); _pending?.Completion?.TrySetException(exception);
            _pending?.Dispose(); _pending = null; _initialized = false;
        }
        finally { _moving = false; if (_pending is null) _idle?.TrySetResult(); if (_requested) RequestObservation(); }
    }
    private static bool SameState(IReadOnlyList<Fence> left, IReadOnlyList<Fence> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i]; var b = right[i];
            if (a.Id != b.Id || a.Name != b.Name || a.X != b.X || a.Y != b.Y || a.Width != b.Width || a.Height != b.Height ||
                a.IsCollapsed != b.IsCollapsed || !a.Icons.SequenceEqual(b.Icons)) return false;
        }
        return true;
    }
    private void SetStatus(string status) { if (status == Status) return; Status = status; StatusChanged?.Invoke(this, EventArgs.Empty); }
    public async Task PrepareExitAsync()
    {
        var started = Stopwatch.GetTimestamp();
        while (_reading || _moving || _pending is not null || _scheduled || ExplorerBusy())
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("Дождитесь завершения перемещения значков и повторите выход.");
            await Task.Delay(20);
        }
        _stopping = true; _timer.Stop(); _releaseTimer.Stop();
        try
        {
            LoadRecovery();
            var returns = _recovery.Concat(IconPlacement.ReturnPositions(_fences))
                .GroupBy(t => t.StableId, FenceIconLayout.IdentityComparer).Select(g => g.Last()).ToArray();
            if (returns.Length == 0) return;
            var snapshot = await _icons.ReadAsync();
            if (!snapshot.Connected)
            {
                // Explorer is absent: keep the return journal for the next healthy session.
                _logger.LogWarning("Explorer unavailable on exit; icon return journal retained");
                return;
            }
            var presentIds = snapshot.Icons.Select(i => i.StableId).ToHashSet(FenceIconLayout.IdentityComparer);
            var targets = FenceIconLayout.ChangedTargets(returns.Where(t => presentIds.Contains(t.StableId)).ToArray(), snapshot.Icons);
            var current = FenceIconLayout.Match(snapshot.Icons);
            var rollback = targets.Select(t => new DesktopIconTarget(t.StableId, current[t.StableId].Position)).ToArray();
            await _transaction.ApplyAsync(targets, rollback, () => { WriteRecovery([]); return true; });
            _logger.LogInformation("Graceful exit restored {Count} desktop icon positions", targets.Count);
        }
        catch
        {
            _stopping = false; _timer.Start(); RequestObservation();
            throw;
        }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _host.IconPositionsChanged -= OnExplorerChanged;
        _host.ExplorerPointer -= OnExplorerPointer;
        _timer.Stop(); _timer.Tick -= OnTick; _releaseTimer.Stop(); _releaseTimer.Tick -= OnReleaseTick;
        _shutdown.Cancel(); _pending?.Completion?.TrySetCanceled(); _pending?.Dispose(); _pending = null;
        _shutdown.Dispose();
        _idle?.TrySetCanceled();
        // Parking survives restarts; parked-icons.json allows explicit recovery without the app.
    }
    private sealed class MoveBatch : IReadOnlyList<DesktopIconTarget>, IDisposable
    {
        private DesktopIconTarget[] _items;
        public bool Verified { get; init; }
        public IReadOnlyList<DesktopIconTarget>? Recovery { get; init; }
        public Action? Present { get; init; }
        public TaskCompletionSource? Completion { get; init; }
        public long RequestedAt { get; } = Stopwatch.GetTimestamp();
        public int Count { get; private set; }
        public MoveBatch(int capacity) => _items = ArrayPool<DesktopIconTarget>.Shared.Rent(capacity);
        public DesktopIconTarget this[int index] => index >= 0 && index < Count ? _items[index] : throw new ArgumentOutOfRangeException(nameof(index));
        public void Add(DesktopIconTarget item) => _items[Count++] = item;
        public IEnumerator<DesktopIconTarget> GetEnumerator() { for (var i = 0; i < Count; i++) yield return _items[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() { ArrayPool<DesktopIconTarget>.Shared.Return(_items, clearArray: true); _items = []; Count = 0; }
    }
}
