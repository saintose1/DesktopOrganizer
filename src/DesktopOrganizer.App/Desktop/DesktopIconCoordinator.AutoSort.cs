using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.App.Desktop;

/// <summary>Auto-sort rules: new unassigned desktop items join the first Fence whose rule matches their name.</summary>
public sealed partial class DesktopIconCoordinator
{
    private HashSet<string>? _knownItems;

    private async Task AutoSortNewItemsAsync(DesktopIconSnapshot snapshot)
    {
        var current = snapshot.Icons.Select(i => i.StableId).ToHashSet(FenceIconLayout.IdentityComparer);
        var known = _knownItems;
        _knownItems = current;
        // The first complete inventory is the baseline: rules never re-sort what is already on the desktop.
        if (known is null || Busy() || !_fences.Any(f => !f.AutoSort.IsEmpty)) return;
        var assigned = _fences.SelectMany(f => f.Icons).Select(i => i.StableId).ToHashSet(FenceIconLayout.IdentityComparer);
        var renamed = _renames.Values.ToHashSet(FenceIconLayout.IdentityComparer); // renamed group items keep their group
        var newcomers = snapshot.Icons.Where(i => !known.Contains(i.StableId) && !assigned.Contains(i.StableId) && !renamed.Contains(i.StableId)).ToArray();
        if (newcomers.Length == 0) return;
        var source = _fences.ToArray(); var revision = _revision; var sequence = _explorerGesture.Sequence;
        var scales = source.ToDictionary(f => f.Id, _host.GetScale);
        var monitors = _monitors.ToArray();
        IReadOnlyList<Fence> changed = FenceAutoSort.AssignNew(source, newcomers, snapshot.Metrics, f => scales[f.Id]);
        if (SameState(source, changed)) return;
        _applyingDrop = true;
        try
        {
            var positioned = await _transaction.ApplyPlannedAsync(final =>
            {
                var present = FenceIconLayout.Match(final.Icons);
                changed = FenceAutoSort.AssignNew(source, newcomers.Where(n => present.ContainsKey(n.StableId)).ToArray(), final.Metrics, f => scales[f.Id]);
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
            _logger.LogInformation("Auto-sort placed {Count} new desktop items into groups", newcomers.Length);
        }
        finally { _applyingDrop = false; }
    }
}
