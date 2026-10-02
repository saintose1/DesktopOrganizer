using DesktopOrganizer.Core.Desktop;

namespace DesktopOrganizer.App.Desktop;

/// <summary>The proposal remains private until Shell positioning is read back and the durable commit succeeds.</summary>
public sealed class IconLayoutTransaction(IDesktopIconService icons)
{
    public async Task<DesktopIconSnapshot> ApplyAsync(IReadOnlyList<DesktopIconTarget> targets, IReadOnlyList<DesktopIconTarget> rollback,
        Func<bool> commit, CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await PositionVerifiedAsync(targets, cancellationToken);
            if (!commit()) throw new InvalidOperationException("Icon layout commit rejected; original positions restored.");
            return snapshot;
        }
        catch (Exception failure)
        {
            // Disposal only cancels observation; the return trip must still be attempted on the Shell worker.
            if (rollback.Count != 0)
            {
                try { await PositionVerifiedAsync(rollback, CancellationToken.None); }
                catch (Exception recoveryFailure)
                {
                    throw new AggregateException("Icon layout failed and original Explorer positions could not be restored.", failure, recoveryFailure);
                }
            }
            throw;
        }
    }
    public async Task<DesktopIconSnapshot> PositionVerifiedAsync(IReadOnlyList<DesktopIconTarget> targets, CancellationToken cancellationToken = default)
    {
        DesktopIconSnapshot? snapshot = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (targets.Count != 0) await icons.PositionAsync(targets, cancellationToken);
            snapshot = await icons.ReadAsync(cancellationToken);
            if (IconPlacement.AtTargets(snapshot, targets)) return snapshot;
            // Explorer can finish its drop after the first positioning call. Reconcile that final write now.
            await Task.Delay(16, cancellationToken);
        }
        throw new InvalidOperationException("Explorer did not retain the requested icon positions; assignment was not saved.");
    }
}
