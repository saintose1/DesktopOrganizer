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

/// <summary>Native drop reconciliation and re-parking of items Explorer pushed onto the desktop.</summary>
public sealed partial class DesktopIconCoordinator
{
    /// <summary>
    /// Explorer clamps every item into its work area on native drops and other layout changes; there is no
    /// offscreen position it leaves alone. Any item of a collapsed group observed on a monitor is parked again
    /// (one bulk, assignments unchanged), so an interrupted or late reflow can never leave items stranded.
    /// </summary>
    /// <summary>
    /// Items of expanded groups belong in their slots. Explorer can re-add a revealed item late, at its first free
    /// grid cell; one verified layout batch returns such items into their Fence.
    /// </summary>
    private void RepairExpandedDrift(DesktopIconSnapshot snapshot)
    {
        if (Busy() || _moving || _pending is not null) return;
        var present = FenceIconLayout.Match(snapshot.Icons);
        var drifted = _fences.Where(f => !f.IsCollapsed).SelectMany(f => f.Icons.Select(i => (Fence: f, Icon: i)))
            .Any(p => present.TryGetValue(p.Icon.StableId, out var icon) &&
                Math.Abs(icon.Position.X - p.Fence.X - p.Icon.OffsetX) + Math.Abs(icon.Position.Y - p.Fence.Y - p.Icon.OffsetY) > 2);
        if (!drifted) return;
        _host.Trace.Mark("Layout.drift");
        _logger.LogInformation("Returning expanded-group items Explorer placed outside their Fence");
        QueueAll();
    }
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
            // Hide newly parked items right away: continuous drags leave no idle observation to do it.
            await SettleConcealmentAsync();
        }
        finally { _applyingDrop = false; }
    }
}
