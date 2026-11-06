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
    private readonly DispatcherTimer _timer, _releaseTimer, _followUp;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<IReadOnlyList<Fence>, bool> _commit;
    private readonly IIconRecoveryStore? _recoveryStore;
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
        // Explorer item WinEvents, Shell change notifications and raw input drive observation.
        // A full inventory read costs hundreds of cross-process calls, so the idle poll is only a safety net.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _followUp = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _followUp.Tick += (_, _) => { _followUp.Stop(); RequestObservation(); };
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
    /// <summary>
    /// Explorer clamps every item into its work area on native drops and other layout changes; there is no
    /// offscreen position it leaves alone. Any item of a collapsed group observed on a monitor is parked again
    /// (one bulk, assignments unchanged), so an interrupted or late reflow can never leave items stranded.
    /// </summary>
    private async Task ReparkStrandedAsync(DesktopIconSnapshot snapshot)
    {
        if (Busy() || _monitors.Count == 0) return;
        var stranded = IconPlacement.DisplacedParking(_fences, [], snapshot, [], _monitors);
        if (stranded.Count == 0) return;
        _applyingDrop = true;
        try
        {
            ApplySnapshot(await _transaction.PositionVerifiedAsync(stranded, _shutdown.Token, drop: true));
            _logger.LogInformation("Re-parked {Count} collapsed-group items Explorer had placed on the desktop", stranded.Count);
        }
        finally { _applyingDrop = false; }
    }
    private bool DropAffectsFence(ExplorerDrop drop) => FenceIconLayout.DropTarget(_fences, drop.Point, _host.GetScale) is not null ||
        _fences.SelectMany(f => f.Icons).Any(i => drop.DraggedIds.Contains(i.StableId, FenceIconLayout.IdentityComparer));
    private async Task ReconcileDropAsync(ExplorerDrop drop)
    {
        var revision = _revision; var sequence = _explorerGesture.Sequence;
        var source = _fences.ToArray(); var monitors = _monitors.ToArray();
        var before = _beforeExplorerDrag;
        var scales = source.ToDictionary(f => f.Id, _host.GetScale);
        IReadOnlyList<Fence> changed = source;
        IReadOnlyList<DesktopIconTarget> targets = [], recovery = [];
        var restoredParking = 0;
        var parked = new HashSet<string>(FenceIconLayout.IdentityComparer);
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
                    var layout = IconPlacement.ChangedFenceTargets(source, changed, monitors, snapshot.Metrics);
                    var parking = IconPlacement.DisplacedParking(changed, before, snapshot, drop.DraggedIds, monitors);
                    restoredParking = parking.Count;
                    foreach (var item in parking) parked.Add(item.StableId);
                    targets = FenceIconLayout.ChangedTargets(layout.Concat(parking)
                        .DistinctBy(t => t.StableId, FenceIconLayout.IdentityComparer).ToArray(), snapshot.Icons);
                    _host.Trace.Mark("Assignment/layout.proposal", drop.DraggedIds.Count);
                    return targets;
                },
                async verified =>
                {
                    // Shell redraw has completed before any durable work or model feedback.
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    // An ordinary drop only re-parked displaced items: nothing durable to commit, so a quick
                    // follow-up press must not reject it and roll the parked items back onto the desktop.
                    if (_revision == revision && SameState(source, changed) && SameState(_fences, changed))
                    {
                        Metrics = verified.Metrics; _host.IconMetrics = Metrics;
                        recovery = IconPlacement.ReturnPositions(changed); // unchanged journal content
                        return true;
                    }
                    if (Busy() || _explorerGesture.Sequence != sequence || _revision != revision) return false;
                    Metrics = verified.Metrics; _host.IconMetrics = Metrics;
                    recovery = IconPlacement.ReturnPositions(changed); PrepareRecovery(recovery);
                    _host.Trace.Mark("Persistence.begin");
                    var accepted = SameState(_fences, changed) || _commit(changed);
                    _host.Trace.Mark("Persistence.end"); return accepted;
                }, _shutdown.Token, parked.Contains);
            ApplySnapshot(positioned); CompleteRecovery(recovery);
            var counters = (_icons as IDesktopPositionDiagnostics)?.PositionCounters ?? default;
            var target = FenceIconLayout.DropTarget(changed, drop.Point, f => scales[f.Id]);
            _logger.LogInformation("Desktop drop reconciled: SelectedCount={Selected}; DraggedItemIdHash={Item}; TargetFence={Fence}; AssignedAfterDrop={Assigned}; MovedTargets={Targets}; RestoredParking={Parked}; BulkCalls={Batches}; ShellRedrawCompletions={Redraw}; ExtraRepaints={Paints}",
                drop.DraggedIds.Count, IdentityHash(drop.DraggedItemId), target?.Id,
                changed.SelectMany(f => f.Icons).Count(i => drop.DraggedIds.Contains(i.StableId, FenceIconLayout.IdentityComparer)),
                targets.Count, restoredParking, counters.Batches - beforeCounters.Batches, counters.RedrawCompletions - beforeCounters.RedrawCompletions, _host.RepaintCount - beforePaints);
            if (_explorerGesture.Sequence == sequence) CompleteGesture();
        }
        finally { _applyingDrop = false; }
    }
    private bool ExplorerBusy() => _explorerGesture.InProgress || _host.HasExplorerGesture || _host.IsLeftButtonDown || _host.IsExplorerDragging;
    private async Task ReconcileMembershipAsync(DesktopIconSnapshot snapshot)
    {
        var present = FenceIconLayout.Match(snapshot.Icons);
        var missing = _fences.SelectMany(f => f.Icons).Select(i => i.StableId).Where(id => !present.ContainsKey(id))
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
    public async Task<AnimatedTransition?> BeginAnimatedTransitionAsync(Fence target, bool startIsCollapsed)
    {
        if (target.Icons.IsEmpty) return null;
        if (!_initialized || !_positioningAvailable || _icons is not IDesktopIconAnimationPositioner positioner)
            throw new InvalidOperationException("Explorer icon animation is not ready.");
        await WaitForPresentationAsync();
        // One snapshot before the shared clock starts. Observation/polling is gated throughout the transition.
        var snapshot = await _icons.ReadAsync(_shutdown.Token);
        if (!snapshot.Connected || !snapshot.CanPosition || snapshot.Error is not null) throw new InvalidOperationException(snapshot.Error ?? "Explorer is unavailable.");
        ApplySnapshot(snapshot);
        if (target.Icons.Any(i => _matched.ContainsKey(i.StableId) && !_recovery.Any(r => FenceIconLayout.IdentityComparer.Equals(r.StableId, i.StableId))))
            throw new InvalidOperationException("Recovery must be armed before animating desktop icons.");
        var proposed = _fences.Select(f => f.Id == target.Id ? target : f).ToArray();
        var final = IconPlacement.Targets(proposed, _monitors, Metrics).Where(t => _matched.ContainsKey(t.StableId)).ToArray();
        var parked = proposed.Select(f => f.Id == target.Id && !f.IsCollapsed ? f.ToggleCollapsed() : f).ToArray();
        var plan = new FenceIconAnimationPlan(target, snapshot.Icons, final, _monitors, startIsCollapsed,
            IconPlacement.Targets(parked, _monitors, Metrics), FenceInteractionGeometry.HeaderHeight * _host.GetScale(target));
        if (plan.Count == 0) return null;
        var shell = await positioner.BeginAnimationAsync(plan.InitialTargets, plan.FinalTargets, _shutdown.Token);
        return new AnimatedTransition(this, plan, shell);
    }
    public sealed class AnimatedTransition : IAsyncDisposable
    {
        private readonly DesktopIconCoordinator _owner;
        private readonly FenceIconAnimationPlan _plan;
        private readonly IDesktopIconAnimationSession _shell;
        private readonly DesktopIconTarget[] _group, _final;
        private readonly List<DesktopIconTarget> _changed;
        private bool _disposed, _parkingFinished;
        public int BulkUpdates { get; private set; }
        public int Redraws { get; private set; }
        public double PositionMilliseconds { get; private set; }
        public double ShellMilliseconds { get; private set; }
        public double RedrawMilliseconds { get; private set; }
        public double ShellFinishMilliseconds { get; private set; }
        public double IconCommitProgress { get; private set; }
        internal AnimatedTransition(DesktopIconCoordinator owner, FenceIconAnimationPlan plan, IDesktopIconAnimationSession shell)
        {
            _owner = owner; _plan = plan; _shell = shell;
            _group = new DesktopIconTarget[plan.Count]; _final = new DesktopIconTarget[plan.FinalTargets.Count];
            plan.Fill(1, true, _final);
            _changed = new(plan.FinalTargets.Count);
        }
        public async Task PresentAsync(FenceAnimationFrame frame, DesktopBounds content, Action present)
        {
            ObjectDisposedException.ThrowIf(_disposed || _owner._disposed, this);
            if (_parkingFinished) { present(); return; }
            _plan.Fill(frame.EasedProgress, false, _group, content);
            var finishParking = _plan.CanFinishParking(_group);
            var completes = frame.Progress >= 1 || finishParking;
            // Commit all parking slots and finish Shell redraw as soon as the last
            // visible glyph leaves. The remaining collapse frames only resize Fence.
            var targets = completes ? _final : _group;
            _changed.Clear();
            foreach (var t in targets)
                if (!_owner._matched.TryGetValue(t.StableId, out var icon) || icon.Position != t.Position) _changed.Add(t);
            var changed = _changed.Count != 0;
            if (!changed && !completes) { present(); return; }
            if (changed) _owner._host.SetIconTargets(_changed);
            var started = Stopwatch.GetTimestamp();
            try
            {
                var shellStarted = Stopwatch.GetTimestamp();
                if (changed) { await _shell.PositionAsync(targets, _owner._shutdown.Token); BulkUpdates++; }
                if (_owner._disposed) throw new OperationCanceledException("Desktop icons are shutting down.");
                foreach (var target in targets)
                {
                    if (!_owner._matched.TryGetValue(target.StableId, out var old)) continue;
                    _owner._matched[target.StableId] = old with { Position = target.Position, Bounds = old.Bounds with
                    { X = old.Bounds.X + target.Position.X - old.Position.X, Y = old.Bounds.Y + target.Position.Y - old.Position.Y } };
                }
                _owner._host.SetIcons(_owner._matched.Values.ToArray());
                present();
                // Redraw was never disabled. Re-enabling it on every frame makes Explorer
                // repaint twice (Shell completion + our damage). Finish the Shell session
                // once on the final visible icon frame, before the collapsed header
                // finishes shrinking; intermediate frames have only the targeted paint.
                if (completes)
                {
                    var finishStarted = Stopwatch.GetTimestamp();
                    await _shell.CompleteFrameAsync(_owner._shutdown.Token);
                    ShellFinishMilliseconds = Stopwatch.GetElapsedTime(finishStarted).TotalMilliseconds;
                    IconCommitProgress = frame.Progress;
                    _parkingFinished = finishParking;
                }
                ShellMilliseconds += Stopwatch.GetElapsedTime(shellStarted).TotalMilliseconds;
                var redrawStarted = Stopwatch.GetTimestamp();
                // Only actual old/new glyph damage. All items are already parked before
                // collapse reaches its final frame; no region restoration exposes old pixels.
                var repaints = _owner._host.RepaintCount;
                _owner._host.FlushDamage();
                Redraws += (int)(_owner._host.RepaintCount - repaints);
                RedrawMilliseconds += Stopwatch.GetElapsedTime(redrawStarted).TotalMilliseconds;
                _owner.LastPresentationAt = Stopwatch.GetTimestamp();
                _owner.FramePresented?.Invoke(started, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            finally { PositionMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
        }
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return; _disposed = true;
            await _shell.DisposeAsync();
        }
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
        _host.IconRenamed -= OnIconRenamed;
        _host.IconRemoved -= OnIconRemoved;
        _host.ExplorerPointer -= OnExplorerPointer;
        _timer.Stop(); _timer.Tick -= OnTick; _releaseTimer.Stop(); _releaseTimer.Tick -= OnReleaseTick;
        _followUp.Stop();
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
