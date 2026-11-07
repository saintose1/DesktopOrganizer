using DesktopOrganizer.Core.Localization;
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
public sealed partial class DesktopIconCoordinator : IDisposable
{
    private readonly IDesktopIconService _icons;
    private readonly IconLayoutTransaction _transaction;
    private readonly Win32DesktopHost _host;
    private readonly ILogger<DesktopIconCoordinator> _logger;
    private readonly DispatcherTimer _timer, _releaseTimer, _followUp, _approachExpiry;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<IReadOnlyList<Fence>, bool> _commit;
    private readonly IIconRecoveryStore? _recoveryStore;
    private readonly IDesktopItemConcealment? _concealment;
    private IReadOnlyList<DesktopIconTarget> _recovery = [];
    private IReadOnlyList<Fence> _fences = [];
    private Dictionary<string, DesktopIcon> _matched = new(FenceIconLayout.IdentityComparer);
    private DesktopIcon[] _beforeExplorerDrag = [];
    private HashSet<string> _previousMissing = new(FenceIconLayout.IdentityComparer);
    private readonly Dictionary<string, string> _renames = new(FenceIconLayout.IdentityComparer);
    private readonly HashSet<string> _removed = new(FenceIconLayout.IdentityComparer);
    private IReadOnlyList<DesktopArea> _monitors = [];
    private MoveBatch? _pending;
    private bool _reading, _moving, _initialized, _disposed, _applyingDrop, _positioningAvailable, _requested, _scheduled, _recoveryLoaded;
    private long _revision;
    private readonly ExplorerGesture _explorerGesture = new();
    private Task? _selectionRead;
    private CancellationTokenSource? _observationRead;
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
    public event Action? GestureSettled;
    public DesktopIconCoordinator(IDesktopIconService icons, Win32DesktopHost host, ILogger<DesktopIconCoordinator> logger,
        Func<IReadOnlyList<Fence>, bool> commit, IIconRecoveryStore? recoveryStore = null)
    {
        _icons = icons; _transaction = new(icons); _host = host; _logger = logger; _commit = commit; _recoveryStore = recoveryStore;
        _concealment = icons as IDesktopItemConcealment;
        // Explorer item WinEvents, Shell change notifications and raw input drive observation.
        // A full inventory read costs hundreds of cross-process calls, so the idle poll is only a safety net.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _followUp = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _followUp.Tick += (_, _) => { _followUp.Stop(); RequestObservation(); };
        _approachExpiry = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _approachExpiry.Tick += OnApproachExpiry;
        _releaseTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick; _releaseTimer.Tick += OnReleaseTick;
        _host.IconPositionsChanged += OnExplorerChanged;
        _host.IconRenamed += OnIconRenamed;
        _host.IconRemoved += OnIconRemoved;
        _host.ExplorerPointer += OnExplorerPointer;
        _host.SetSelectionReader(icons as IDesktopSelectionReader);
    }
    public void Start() { _timer.Start(); RequestObservation(); }
    public void SetFences(IReadOnlyList<Fence> fences)
    {
        // A title/style preview updates our snapshot without positioning or redrawing Explorer icons.
        var presentationOnly = fences.Count == _fences.Count && fences.Zip(_fences).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.X == pair.Second.X && pair.First.Y == pair.Second.Y &&
            pair.First.Width == pair.Second.Width && pair.First.Height == pair.Second.Height &&
            pair.First.IsCollapsed == pair.Second.IsCollapsed && pair.First.Icons.SequenceEqual(pair.Second.Icons));
        _fences = fences; _revision++;
        if (presentationOnly) return;
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
            _observationRead?.Cancel(); // Do not make the real gesture wait behind a full idle inventory read.
            if (input.SelectionSnapshot is null) _host.Trace.Begin();
            _previousMissing.Clear();
            _beforeExplorerDrag = _matched.Values.ToArray();
            _explorerGesture.Begin(input.Point, input.Background); _selectionRead = null;
            if (input.SelectionSnapshot is not null)
                _selectionRead = CaptureSelectionAsync(input.SelectionSnapshot, _explorerGesture.Sequence);
            else if (!input.Background && _icons is IDesktopSelectionReader reader)
                _selectionRead = CaptureSelectionAsync(reader.ReadSelectionAsync(input.Point, _shutdown.Token), _explorerGesture.Sequence);
            // A press may start a drag: groups prepared for a hover leave Explorer now, while the drag
            // is still in progress, instead of waiting for the pointer to leave the Fence row.
            _approachedAt.Clear();
            _ = ConcealParkedNowAsync();
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
            _explorerGesture.Release(input.Point, input.DesktopTarget); _releaseTimer.Start(); RequestObservation(DispatcherPriority.Input);
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
        // Explorer's capture end follows its native drop (and its reflow of parked items):
        // re-park immediately instead of waiting for the next release timer tick.
        RequestObservation(_explorerGesture.Phase == ExplorerGesturePhase.PostDropReconciliation
            ? DispatcherPriority.Input : DispatcherPriority.Background);
    }
    private void OnIconRenamed(string before, string after)
    {
        if (!_fences.SelectMany(f => f.Icons).Any(i => FenceIconLayout.IdentityComparer.Equals(i.StableId, before)) &&
            !_renames.Values.Contains(before, FenceIconLayout.IdentityComparer)) return;
        foreach (var original in _renames.Where(p => FenceIconLayout.IdentityComparer.Equals(p.Value, before)).Select(p => p.Key).ToArray())
            _renames[original] = after;
        _renames[before] = after;
        _revision++; // Invalidate a proposal computed before a newer native rename pair.
        RequestObservation();
    }
    private void OnIconRemoved(string identity)
    {
        if (_fences.SelectMany(f => f.Icons).Any(i => FenceIconLayout.IdentityComparer.Equals(i.StableId, identity)) && _removed.Add(identity)) _revision++;
        RequestObservation();
    }
    private void OnReleaseTick(object? sender, EventArgs args)
    {
        if (ExplorerBusy()) return;
        _releaseTimer.Stop(); RequestObservation(DispatcherPriority.Input);
    }
    private void RequestObservation(DispatcherPriority priority = DispatcherPriority.Background)
    {
        if (_disposed || _stopping) return;
        _requested = true;
        if (_reading || _moving || _scheduled) return;
        _scheduled = true;
        _dispatcher.BeginInvoke(priority, new Action(() => { _scheduled = false; OnTick(this, EventArgs.Empty); }));
    }
    private async void OnTick(object? sender, EventArgs args)
    {
        if (_disposed || _stopping || _reading || _moving || InteractionActive || _host.HasActiveInteraction) return;
        if (ExplorerBusy()) { _releaseTimer.Start(); return; }
        _reading = true; _requested = false;
        var sequence = _explorerGesture.Sequence;
        try
        {
            if (_selectionRead is not null) await _selectionRead;
            if (Busy() || _explorerGesture.Sequence != sequence) { RequestObservation(DispatcherPriority.Input); return; }
            _host.FinishNativeSelection();
            if (_initialized && _positioningAvailable && Enabled && _host.PresentationConnected &&
                _explorerGesture.Drop is { DesktopTarget: true } drop &&
                (DropAffectsFence(drop) || _fences.Any(f => f.IsCollapsed && !f.Icons.IsEmpty)))
            {
                // Explorer's reflow of parked items is on screen right now: no extra cross-process
                // presentation checks before the re-park batch (the next observation performs them).
                _host.Trace.Mark("Drop.tick");
                try { await ReconcileDropAsync(drop); }
                catch (Exception) when (!_disposed) { _retryDropRepair = true; throw; }
                return;
            }
            // Shell refresh can reset the ListView background before item enumeration becomes consistent.
            _host.ReconcileExplorerPresentation();
            _observationRead = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var snapshot = await _icons.ReadAsync(_observationRead.Token);
            // An idle read started before a complete fast drag must not consume that newer drop.
            if (Busy() || _explorerGesture.Sequence != sequence) { RequestObservation(DispatcherPriority.Input); return; }
            SetStatus($"Desktop icons: {(snapshot.Connected ? snapshot.Icons.Count.ToString() : "Disconnected")}" +
                (snapshot.Error is null ? snapshot.Notice is null ? "" : " · " + snapshot.Notice : " · " + snapshot.Error));
            if (!snapshot.Connected) { _previousMissing.Clear(); _initialized = false; _positioningAvailable = false; return; }
            // Retain the last complete snapshot and readiness through transient Explorer enumeration failures.
            // Shell membership changes often make one enumeration inconsistent; retry soon, not at the safety poll.
            if (snapshot.Error is not null) { _previousMissing.Clear(); _followUp.Start(); return; }
            _host.ReconcileExplorerPresentation();
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
            if (available) await ReparkStrandedAsync(snapshot);
            if (available) RepairExpandedDrift(snapshot);
            if (available) await AutoSortNewItemsAsync(snapshot);
            if (available) await SettleConcealmentAsync();
            if (available) await ReconcileMembershipAsync(snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _host.Trace.Mark("Reconciliation.failure");
            _logger.LogWarning(exception, "Desktop icon reconciliation failed; durable assignment was not advanced");
            SetStatus("Desktop icons: " + exception.Message);
            if (_explorerGesture.Phase == ExplorerGesturePhase.PostDropReconciliation) CompleteGesture();
        }
        finally
        {
            _observationRead?.Dispose(); _observationRead = null;
            _reading = false;
            if (_pending is not null && !_moving && !_disposed) MoveLatest();
            else if (_retryDropRepair)
            {
                // A failed drop repair leaves Explorer's reflowed items visible; re-observe now (the ordinary
                // path re-parks stranded items) rather than at the slow safety poll.
                _retryDropRepair = false; RequestObservation(DispatcherPriority.Input);
            }
            else if (_requested) RequestObservation(_explorerGesture.Phase == ExplorerGesturePhase.PostDropReconciliation
                ? DispatcherPriority.Input : DispatcherPriority.Background);
        }
    }
    private bool _retryDropRepair;
    private bool ExplorerBusy() => _explorerGesture.InProgress || _host.HasExplorerGesture || _host.IsLeftButtonDown || _host.IsExplorerDragging;
    private async Task ReconcileMembershipAsync(DesktopIconSnapshot snapshot)
    {
        var present = FenceIconLayout.Match(snapshot.Icons);
        // Items this application hid are absent from the view by design, not removed from the desktop.
        var concealed = ConcealedIds();
        var missing = _fences.SelectMany(f => f.Icons).Select(i => i.StableId).Where(id => !present.ContainsKey(id) && !concealed.Contains(id))
            .ToHashSet(FenceIconLayout.IdentityComparer);
        var confirmed = missing.Intersect(_previousMissing, FenceIconLayout.IdentityComparer).ToHashSet(FenceIconLayout.IdentityComparer);
        _removed.RemoveWhere(present.ContainsKey); // A restored item invalidates an earlier DELETE, even before the first missing read.
        // A Shell removal event or a confirmed missing filesystem item is required, even after two complete reads.
        var removed = _removed.ToHashSet(FenceIconLayout.IdentityComparer);
        if (confirmed.Count != 0)
            confirmed = await Task.Run(() => confirmed.Where(id => removed.Contains(id) || DesktopIconService.IsConfirmedMissingFile(id))
                .ToHashSet(FenceIconLayout.IdentityComparer), _shutdown.Token);
        if (Busy()) return;
        _previousMissing = missing;
        // A removal needs a second complete read; do not wait for the slow safety poll.
        if (missing.Count > confirmed.Count) _followUp.Start();
        if (confirmed.Count == 0 && _renames.Count == 0) return;
        var source = _fences.ToArray(); var revision = _revision; var sequence = _explorerGesture.Sequence;
        var scales = source.ToDictionary(f => f.Id, _host.GetScale);
        var monitors = _monitors.ToArray(); var renames = new Dictionary<string, string>(_renames, FenceIconLayout.IdentityComparer);
        IReadOnlyList<Fence> changed = FenceIconLayout.ReconcileMembership(source, snapshot, confirmed, renames, f => scales[f.Id]);
        if (SameState(source, changed)) return;
        _applyingDrop = true;
        try
        {
            var positioned = await _transaction.ApplyPlannedAsync(final =>
            {
                changed = FenceIconLayout.ReconcileMembership(source, final, confirmed, renames, f => scales[f.Id]);
                return FenceIconLayout.ChangedTargets(IconPlacement.ChangedFenceTargets(source, changed, monitors, final.Metrics), final.Icons);
            }, verified =>
            {
                if (Busy() || _revision != revision || _explorerGesture.Sequence != sequence) return Task.FromResult(false);
                var recovery = IconPlacement.ReturnPositions(changed); PrepareRecovery(recovery);
                var accepted = SameState(source, changed) || _commit(changed);
                if (accepted) CompleteRecovery(recovery);
                return Task.FromResult(accepted);
            }, _shutdown.Token);
            ApplySnapshot(positioned);
            var assigned = _fences.SelectMany(f => f.Icons).Select(i => i.StableId).ToHashSet(FenceIconLayout.IdentityComparer);
            foreach (var alias in renames.Keys.Where(id => !assigned.Contains(id))) _renames.Remove(alias);
            _removed.RemoveWhere(id => !assigned.Contains(id));
            _logger.LogInformation("Explorer membership reconciled: ConfirmedMissing={Missing}; RenamePairs={Renamed}; Assigned={Assigned}",
                confirmed.Count, renames.Count, assigned.Count);
        }
        finally { _applyingDrop = false; }
    }
    private bool ExplorerPositioningBusy() => _host.HasExplorerGesture ||
        _explorerGesture.BlocksFencePresentation(InteractionActive || _host.HasActiveInteraction, _host.IsExplorerDragging);
    private bool Busy() => _disposed || InteractionActive || _host.HasActiveInteraction || ExplorerBusy();
    private void CompleteGesture()
    {
        _host.Trace.Complete(_explorerGesture.Drop is null ? "selection/click/cancel" : "drop");
        _explorerGesture.Reset(); _selectionRead = null; _beforeExplorerDrag = [];
        if (_layoutDeferred) { _layoutDeferred = false; QueueAll(); }
        GestureSettled?.Invoke();
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
        if (!fence.IsCollapsed && ConcealedIds().Overlaps(fence.Icons.Select(i => i.StableId)))
        {
            await RevealGroupAsync(fence);
            var revealed = await _icons.ReadAsync(_shutdown.Token);
            if (revealed.Connected && revealed.Error is null) ApplySnapshot(revealed);
        }
        // Return positions are armed during startup/assignment/end-of-gesture, never written on the collapse visual path.
        if (fence.IsCollapsed && fence.Icons.Any(i => !_recovery.Any(r => FenceIconLayout.IdentityComparer.Equals(r.StableId, i.StableId))))
            throw new InvalidOperationException("Icon recovery must be armed before collapse.");
        var proposed = _fences.Select(f => f.Id == fence.Id ? fence : f).ToArray();
        var targets = IconPlacement.Targets(proposed, _monitors, Metrics).Where(t => _matched.ContainsKey(t.StableId)).ToArray();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batch = new MoveBatch(Math.Max(1, targets.Length)) { Present = present, Completion = completion };
        foreach (var target in targets) batch.Add(target);
        Enqueue(batch); await completion.Task;
        RequestObservation(); // settles the Hidden attribute layer for the new state
    }
    public async Task VerifyTransitionAsync(Fence fence)
    {
        var proposed = _fences.Select(f => f.Id == fence.Id ? fence : f).ToArray();
        var snapshot = await _icons.ReadAsync(_shutdown.Token);
        var present = FenceIconLayout.Match(snapshot.Icons);
        // Only this group's items that Explorer currently shows: items of other groups (or this one) may be
        // hidden by the attribute layer meanwhile, which is not a failed transition.
        var own = fence.Icons.Select(i => i.StableId).ToHashSet(FenceIconLayout.IdentityComparer);
        var targets = IconPlacement.Targets(proposed, _monitors, Metrics).Where(t => own.Contains(t.StableId) && present.ContainsKey(t.StableId)).ToArray();
        if (IconPlacement.AtTargets(snapshot, targets)) return;
        // Items that Explorer re-added late (after a reveal) are placed once; only a second miss rolls the
        // transition back. Rolling back for a lagging item looked like "opens and closes again" on hover.
        var late = FenceIconLayout.ChangedTargets(targets, snapshot.Icons);
        _host.Trace.Mark("Verify.correct", late.Count);
        ApplySnapshot(await _transaction.PositionVerifiedAsync(late, _shutdown.Token));
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
                        _requested = true; // settle the Hidden attribute layer for the restored layout
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
                    _host.ReconcileExplorerPresentation();
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
                throw new InvalidOperationException(Texts.T("Дождитесь завершения перемещения значков и повторите выход."));
            await Task.Delay(20);
        }
        _stopping = true; _timer.Stop(); _releaseTimer.Stop();
        try
        {
            LoadRecovery();
            var returns = _recovery.Concat(IconPlacement.ReturnPositions(_fences))
                .GroupBy(t => t.StableId, FenceIconLayout.IdentityComparer).Select(g => g.Last()).ToArray();
            if (_concealment is not null && _concealment.Concealed.Count != 0)
            {
                // Every item this application hid is shown again before exit, directly at its return position.
                var positions = returns.ToDictionary(t => t.StableId, t => t.Position, FenceIconLayout.IdentityComparer);
                await _concealment.RevealAsync(_concealment.Concealed
                    .Select(id => (id, positions.TryGetValue(id, out var p) ? (DesktopPoint?)p : null)).ToArray());
                _logger.LogInformation("Exit returned all hidden collapsed-group items");
            }
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
        _host.IconRenamed -= OnIconRenamed;
        _host.IconRemoved -= OnIconRemoved;
        _host.ExplorerPointer -= OnExplorerPointer;
        _timer.Stop(); _timer.Tick -= OnTick; _releaseTimer.Stop(); _releaseTimer.Tick -= OnReleaseTick;
        _followUp.Stop(); _approachExpiry.Stop(); _approachExpiry.Tick -= OnApproachExpiry;
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
