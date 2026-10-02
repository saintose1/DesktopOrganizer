using System.Buffers;
using System.Collections;
using System.Diagnostics;
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
    private Dictionary<string, DesktopIcon> _baseline = new(FenceIconLayout.IdentityComparer);
    private IReadOnlyList<DesktopArea> _monitors = [];
    private MoveBatch? _pending;
    private bool _reading, _moving, _initialized, _disposed, _applyingDrop, _positioningAvailable, _requested, _scheduled, _recoveryLoaded;
    private long _revision;
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
    }
    public void Start() { _timer.Start(); RequestObservation(); }
    public void SetFences(IReadOnlyList<Fence> fences)
    {
        _fences = fences; _revision++;
        if (_initialized && _positioningAvailable && !_applyingDrop && !InteractionActive && !_host.HasActiveInteraction) QueueAll();
    }
    private void OnExplorerChanged()
    {
        if (_disposed || _moving || _applyingDrop || InteractionActive || _host.HasActiveInteraction) return;
        if (_host.IsLeftButtonDown || _host.IsExplorerDragging) { _releaseTimer.Start(); return; }
        RequestObservation();
    }
    private void OnReleaseTick(object? sender, EventArgs args)
    {
        if (_host.IsLeftButtonDown || _host.IsExplorerDragging) return;
        _releaseTimer.Stop(); RequestObservation();
    }
    private void RequestObservation()
    {
        if (_disposed) return;
        _requested = true;
        if (_reading || _moving || _scheduled) return;
        _scheduled = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { _scheduled = false; OnTick(this, EventArgs.Empty); }));
    }
    private async void OnTick(object? sender, EventArgs args)
    {
        if (_disposed || _reading || _moving || InteractionActive || _host.HasActiveInteraction) return;
        if (_host.IsLeftButtonDown || _host.IsExplorerDragging) { _releaseTimer.Start(); return; }
        _reading = true; _requested = false;
        try
        {
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
                _baseline = new(_matched, FenceIconLayout.IdentityComparer); _initialized = true; _positioningAvailable = available;
                if (available) { _monitors = _host.GetMonitorAreas(); QueueAll(); }
                return;
            }
            _positioningAvailable = available;
            if (!available) { _baseline = new(_matched, FenceIconLayout.IdentityComparer); return; }
            var moved = Moved(snapshot);
            if (moved.Count == 0) return;
            // Read Explorer's final write after release, rather than accepting a transient enumeration.
            await Task.Delay(16, _shutdown.Token);
            var final = await _icons.ReadAsync(_shutdown.Token);
            if (Busy() || !final.Connected || !final.CanPosition || final.Error is not null) return;
            var settled = Moved(final);
            if (!SameMoved(moved, settled)) { RequestObservation(); return; }
            _matched = FenceIconLayout.Match(final.Icons);
            var collapsed = _fences.Where(f => f.IsCollapsed).SelectMany(f => f.Icons).Select(i => i.StableId).ToHashSet(FenceIconLayout.IdentityComparer);
            var visibleMoved = settled.Where(i => !collapsed.Contains(i.StableId)).ToArray();
            if (visibleMoved.Length == 0) { QueueAll(); return; }
            var revision = _revision;
            var changed = FenceIconLayout.ApplyMovedIcons(_fences, visibleMoved, Metrics, _host.GetScale);
            var targets = IconPlacement.Targets(changed, _monitors, Metrics).Where(t => _matched.ContainsKey(t.StableId)).ToArray();
            var rollback = targets.Select(t => new DesktopIconTarget(t.StableId, _matched[t.StableId].Position)).ToArray();
            var recovery = IconPlacement.ReturnPositions(changed);
            PrepareRecovery(recovery);
            _applyingDrop = true;
            try
            {
                _host.SetIconTargets(targets);
                var positioned = await _transaction.ApplyAsync(targets, rollback,
                    () => !Busy() && _revision == revision && (SameState(_fences, changed) || _commit(changed)), _shutdown.Token);
                ApplySnapshot(positioned); _host.FlushDamage(); CompleteRecovery(recovery);
                _logger.LogInformation("Desktop drop reconciled: ChangedIcons={Count}; PositionedIcons={Targets}; persisted after verified Shell layout", visibleMoved.Length, targets.Length);
            }
            finally { _applyingDrop = false; }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Desktop icon reconciliation failed; durable assignment was not advanced");
            SetStatus("Desktop icons: " + exception.Message); _initialized = false;
        }
        finally
        {
            _reading = false;
            if (_pending is not null && !_moving && !_disposed) MoveLatest();
            else if (_requested) RequestObservation();
        }
    }
    private bool Busy() => _disposed || InteractionActive || _host.HasActiveInteraction || _host.IsLeftButtonDown || _host.IsExplorerDragging;
    private List<DesktopIcon> Moved(DesktopIconSnapshot snapshot) => snapshot.Icons.Where(i =>
        !_baseline.TryGetValue(i.StableId, out var before) || Distance(before.Position, i.Position) > 1).ToList();
    private void ApplySnapshot(DesktopIconSnapshot snapshot)
    {
        _matched = FenceIconLayout.Match(snapshot.Icons); _baseline = new(_matched, FenceIconLayout.IdentityComparer);
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
        try
        {
            LoadRecovery(); _monitors = _host.GetMonitorAreas();
            var targets = IconPlacement.Targets(_fences, _monitors, Metrics);
            var ids = targets.Select(t => t.StableId).ToHashSet(FenceIconLayout.IdentityComparer);
            var orphaned = _recovery.Where(t => !ids.Contains(t.StableId)).ToArray();
            var batch = new MoveBatch(Math.Max(1, targets.Count + orphaned.Length)) { Recovery = IconPlacement.ReturnPositions(_fences), Verified = true };
            foreach (var target in targets.Concat(orphaned)) if (_matched.ContainsKey(target.StableId)) batch.Add(target);
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
                            _matched[target.StableId] = updated; _baseline[target.StableId] = updated;
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
    private static double Distance(DesktopPoint a, DesktopPoint b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
    private static bool SameMoved(IReadOnlyList<DesktopIcon> left, IReadOnlyList<DesktopIcon> right) => left.Count == right.Count &&
        left.All(a => right.Any(b => FenceIconLayout.IdentityComparer.Equals(a.StableId, b.StableId) && Distance(a.Position, b.Position) <= 1));
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
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _host.IconPositionsChanged -= OnExplorerChanged;
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
