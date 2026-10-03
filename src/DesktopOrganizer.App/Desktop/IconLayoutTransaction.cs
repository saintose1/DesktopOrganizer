using DesktopOrganizer.Core.Desktop;

namespace DesktopOrganizer.App.Desktop;

/// <summary>The proposal remains private until Shell positioning is read back and the durable commit succeeds.</summary>
public sealed class IconLayoutTransaction(IDesktopIconService icons)
{
    public async Task<DesktopIconSnapshot> ApplyPlannedAsync(Func<DesktopIconSnapshot, IReadOnlyList<DesktopIconTarget>> layout,
        Func<DesktopIconSnapshot, Task<bool>> commit, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DesktopIconTarget> targets = [], rollback = [];
        IReadOnlyList<DesktopIconTarget> Plan(DesktopIconSnapshot snapshot)
        {
            targets = layout(snapshot);
            var matched = FenceIconLayout.Match(snapshot.Icons);
            rollback = targets.Where(t => matched.ContainsKey(t.StableId)).Select(t => new DesktopIconTarget(t.StableId, matched[t.StableId].Position)).ToArray();
            return targets;
        }
        try
        {
            DesktopIconSnapshot snapshot;
            if (icons is IDesktopDropReconciler native)
            {
                await native.ReconcileDropAsync(Plan, cancellationToken);
                snapshot = await ReadVerifiedAsync(targets, cancellationToken);
            }
            else
            {
                Plan(await icons.ReadAsync(cancellationToken));
                snapshot = await PositionVerifiedAsync(targets, cancellationToken, drop: true);
            }
            if (!await commit(snapshot)) throw new InvalidOperationException("Icon layout commit rejected; original positions restored.");
            return snapshot;
        }
        catch (Exception failure)
        {
            await RollbackAsync(rollback, failure); throw;
        }
    }
    private async Task<DesktopIconSnapshot> ReadVerifiedAsync(IReadOnlyList<DesktopIconTarget> targets, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var snapshot = await icons.ReadAsync(cancellationToken);
            if (IconPlacement.AtTargets(snapshot, targets)) return snapshot;
            await Task.Delay(16, cancellationToken);
        }
        throw new InvalidOperationException("Explorer did not retain the requested icon positions; assignment was not saved.");
    }
    private async Task RollbackAsync(IReadOnlyList<DesktopIconTarget> rollback, Exception failure)
    {
        if (rollback.Count == 0) return;
        try { await PositionVerifiedAsync(rollback, CancellationToken.None); }
        catch (Exception recoveryFailure)
        { throw new AggregateException("Icon layout failed and original Explorer positions could not be restored.", failure, recoveryFailure); }
    }
    public Task<DesktopIconSnapshot> ApplyAsync(IReadOnlyList<DesktopIconTarget> targets, IReadOnlyList<DesktopIconTarget> rollback,
        Func<bool> commit, CancellationToken cancellationToken = default)
        => ApplyDeferredAsync(targets, rollback, () => Task.FromResult(commit()), cancellationToken);

    public async Task<DesktopIconSnapshot> ApplyDeferredAsync(IReadOnlyList<DesktopIconTarget> targets, IReadOnlyList<DesktopIconTarget> rollback,
        Func<Task<bool>> commit, CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await PositionVerifiedAsync(targets, cancellationToken, drop: true);
            if (!await commit()) throw new InvalidOperationException("Icon layout commit rejected; original positions restored.");
            return snapshot;
        }
        catch (Exception failure)
        {
            // Disposal only cancels observation; the return trip must still be attempted on the Shell worker.
            await RollbackAsync(rollback, failure);
            throw;
        }
    }
    public async Task<DesktopIconSnapshot> PositionVerifiedAsync(IReadOnlyList<DesktopIconTarget> targets, CancellationToken cancellationToken = default, bool drop = false)
    {
        DesktopIconSnapshot? snapshot = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (targets.Count != 0 && (!drop || attempt == 0))
            {
                if (drop && icons is IDesktopDropPositioner batch) await batch.PositionDropAsync(targets, cancellationToken);
                else await icons.PositionAsync(targets, cancellationToken);
            }
            snapshot = await icons.ReadAsync(cancellationToken);
            if (IconPlacement.AtTargets(snapshot, targets)) return snapshot;
            // Drop has one visual write. Retry readback only; a competing Explorer write rejects
            // the proposal and restores the original state instead of visibly positioning again.
            await Task.Delay(16, cancellationToken);
        }
        throw new InvalidOperationException("Explorer did not retain the requested icon positions; assignment was not saved.");
    }
}
