namespace DesktopOrganizer.Core.Desktop;

public enum ExplorerGesturePhase { Idle, Pressed, MarqueeSelection, SingleDrag, MultiDrag, PostDropReconciliation }
public enum ExplorerPointerPhase { Pressed, DragStarted, Released, Cancelled }
public readonly record struct ExplorerPointerEvent(ExplorerPointerPhase Phase, DesktopPoint Point, bool Background, bool DesktopTarget)
{
    public Task<DesktopSelection>? SelectionSnapshot { get; init; }
}
public sealed record DesktopSelection(IReadOnlyList<string> SelectedIds, string? DraggedItemId)
{
    public IReadOnlyList<DesktopIconTarget> InitialPositions { get; init; } = [];
}
public interface IDesktopSelectionReader
{
    Task<DesktopSelection> ReadSelectionAsync(DesktopPoint pressPoint, CancellationToken cancellationToken = default);
}
public interface IDesktopDropPositioner
{
    Task PositionDropAsync(IReadOnlyList<DesktopIconTarget> targets, CancellationToken cancellationToken = default);
}
public interface IDesktopDropReconciler
{
    // The callback computes a pure layout from a final native snapshot before the
    // short positioning/redraw lease. Empty targets do not acquire a redraw lease.
    // It must not do UI, persistence, additional COM calls or asynchronous work.
    Task ReconcileDropAsync(Func<DesktopIconSnapshot, IReadOnlyList<DesktopIconTarget>> layout, CancellationToken cancellationToken = default);
}
public readonly record struct DesktopPositionCounters(long Batches, long RedrawCompletions);
public interface IDesktopPositionDiagnostics { DesktopPositionCounters PositionCounters { get; } }
public sealed record ExplorerDrop(IReadOnlyList<string> DraggedIds, string DraggedItemId, DesktopPoint Point, bool DesktopTarget)
{
    public IReadOnlyList<DesktopIconTarget> InitialPositions { get; init; } = [];
}

/// <summary>Immutable selection snapshot for one native gesture; late reads cannot attach to a later press.</summary>
public sealed class ExplorerGesture
{
    public long Sequence { get; private set; }
    public ExplorerGesturePhase Phase { get; private set; }
    public DesktopPoint PressPoint { get; private set; }
    public DesktopPoint DropPoint { get; private set; }
    public bool DesktopTarget { get; private set; }
    private bool _dragStarted;
    private string? _item;
    private string[] _ids = [];
    private DesktopIconTarget[] _initial = [];
    public bool InProgress => Phase is ExplorerGesturePhase.Pressed or ExplorerGesturePhase.MarqueeSelection or ExplorerGesturePhase.SingleDrag or ExplorerGesturePhase.MultiDrag;
    public bool BlocksFencePresentation(bool ownFenceInteraction, bool explorerCapture) => InProgress || (!ownFenceInteraction && explorerCapture);
    public void Begin(DesktopPoint point, bool background)
    {
        Sequence++; PressPoint = point; _dragStarted = false; _item = null; _ids = []; _initial = [];
        Phase = background ? ExplorerGesturePhase.MarqueeSelection : ExplorerGesturePhase.Pressed;
    }
    public bool StartDrag()
    {
        if (Phase != ExplorerGesturePhase.Pressed || _dragStarted) return false;
        _dragStarted = true; UpdateDragPhase(); return true;
    }
    public bool Capture(long sequence, DesktopSelection selection)
    {
        if (sequence != Sequence || Phase is ExplorerGesturePhase.Idle or ExplorerGesturePhase.MarqueeSelection) return false;
        if (selection.DraggedItemId is null)
        {
            if (Phase != ExplorerGesturePhase.PostDropReconciliation) Phase = ExplorerGesturePhase.MarqueeSelection;
            return false;
        }
        _item = selection.DraggedItemId;
        _ids = selection.SelectedIds.Contains(_item, FenceIconLayout.IdentityComparer)
            ? selection.SelectedIds.Distinct(FenceIconLayout.IdentityComparer).ToArray() : [_item];
        _initial = selection.InitialPositions.Where(t => _ids.Contains(t.StableId, FenceIconLayout.IdentityComparer)).ToArray();
        UpdateDragPhase();
        return true;
    }
    private void UpdateDragPhase()
    {
        if (_dragStarted && _item is not null && Phase != ExplorerGesturePhase.PostDropReconciliation)
            Phase = _ids.Length > 1 ? ExplorerGesturePhase.MultiDrag : ExplorerGesturePhase.SingleDrag;
    }
    public void Release(DesktopPoint point, bool desktopTarget)
    {
        if (!InProgress) return;
        DropPoint = point; DesktopTarget = desktopTarget; Phase = ExplorerGesturePhase.PostDropReconciliation;
    }
    public ExplorerDrop? Drop => Phase == ExplorerGesturePhase.PostDropReconciliation && _dragStarted && _item is not null
        ? new(_ids.ToArray(), _item, DropPoint, DesktopTarget) { InitialPositions = _initial.ToArray() } : null;
    public void Cancel()
    {
        Sequence++; Phase = ExplorerGesturePhase.PostDropReconciliation; _item = null; _ids = []; _initial = []; _dragStarted = false;
    }
    public void Reset() { Sequence++; Phase = ExplorerGesturePhase.Idle; _item = null; _ids = []; _initial = []; _dragStarted = false; }
}
