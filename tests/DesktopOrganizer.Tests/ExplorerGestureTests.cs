using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Tests;

/// <summary>Selection snapshot and drop policy only; these are not Explorer/OLE integration tests.</summary>
public sealed class ExplorerGestureTests
{
    private static DesktopIcon Icon(string id, double x, double y) => new(0, id, "Icon", new(x, y), new(x, y), new(x, y, 90, 90));
    private static Fence FenceAt(double x = 100) => new(Guid.NewGuid(), "Work", x, 100, 300, 250);
    private static readonly IconGridMetrics Metrics = new(100, 110, 90, 90);
    private static ExplorerDrop Drop(string[] ids, DesktopPoint? point = null, bool desktop = true) => new(ids, ids[0], point ?? new(150, 170), desktop);

    [Fact]
    public void BackgroundGestureIsMarqueeAndNeverAnIconDrop()
    {
        var gesture = new ExplorerGesture(); gesture.Begin(new(100, 100), true);
        Assert.Equal(ExplorerGesturePhase.MarqueeSelection, gesture.Phase); Assert.True(gesture.InProgress);
        Assert.False(gesture.StartDrag());
        gesture.Release(new(300, 400), true);
        Assert.Equal(ExplorerGesturePhase.PostDropReconciliation, gesture.Phase); Assert.False(gesture.InProgress); Assert.Null(gesture.Drop);
    }
    [Fact]
    public void MultiSelectionIsCapturedBeforeReleaseAndSurvivesSelectionChanges()
    {
        var selected = new List<string> { "A", "B", "C", "D" };
        var gesture = new ExplorerGesture(); gesture.Begin(new(5, 5), false); Assert.True(gesture.StartDrag());
        Assert.True(gesture.Capture(gesture.Sequence, new(selected, "B")));
        selected.Clear(); Assert.Equal(ExplorerGesturePhase.MultiDrag, gesture.Phase);
        gesture.Release(new(150, 170), true);
        Assert.Equal(["A", "B", "C", "D"], gesture.Drop!.DraggedIds); Assert.Equal("B", gesture.Drop.DraggedItemId);
    }
    [Fact]
    public void UnselectedPressedItemDoesNotDragThePreviousSelection()
    {
        var gesture = new ExplorerGesture(); gesture.Begin(new(0, 0), false); gesture.StartDrag();
        gesture.Capture(gesture.Sequence, new(["A", "B"], "C"));
        Assert.Equal(ExplorerGesturePhase.SingleDrag, gesture.Phase);
        gesture.Release(new(150, 170), true); Assert.Equal(["C"], gesture.Drop!.DraggedIds);
    }
    [Fact]
    public void ReadingSelectionCanFinishAfterReleaseButNotAfterANewPress()
    {
        var gesture = new ExplorerGesture(); gesture.Begin(new(0, 0), false); gesture.StartDrag(); var first = gesture.Sequence;
        gesture.Release(new(150, 170), true);
        Assert.True(gesture.Capture(first, new(["A", "B"], "A"))); Assert.Equal(2, gesture.Drop!.DraggedIds.Count);
        gesture.Begin(new(0, 0), false); gesture.StartDrag();
        Assert.False(gesture.Capture(first, new(["A", "B"], "A"))); Assert.Null(gesture.Drop);
    }
    [Fact]
    public void CancellationAndClickCannotBecomeAGroupDrop()
    {
        var gesture = new ExplorerGesture(); gesture.Begin(new(0, 0), false);
        Assert.True(gesture.Capture(gesture.Sequence, new(["A"], "A")));
        gesture.Release(new(150, 170), true); Assert.Null(gesture.Drop);
        gesture.Reset(); Assert.Equal(ExplorerGesturePhase.Idle, gesture.Phase);
    }
    [Fact]
    public void PressSnapshotIsRetainedBeforeMotionAndClickDoesNotBecomeDrop()
    {
        var selected = new List<string> { "A", "B", "C", "D" };
        var positions = selected.Select((id, n) => new DesktopIconTarget(id, new(100, n * 100))).ToList();
        var gesture = new ExplorerGesture(); gesture.Begin(new(100, 100), false);
        Assert.True(gesture.Capture(gesture.Sequence, new(selected, "B") { InitialPositions = positions }));
        Assert.Equal(ExplorerGesturePhase.Pressed, gesture.Phase); Assert.Null(gesture.Drop);
        selected.Clear(); positions.Clear(); Assert.True(gesture.StartDrag());
        gesture.Release(new(150, 170), true);
        Assert.Equal(4, gesture.Drop!.DraggedIds.Count); Assert.Equal(4, gesture.Drop.InitialPositions.Count);
    }
    [Fact]
    public void CancelledNativeDragCannotFallBackToPerIconBoundsOrAcceptALateRead()
    {
        var gesture = new ExplorerGesture(); gesture.Begin(new(0, 0), false); gesture.StartDrag(); var sequence = gesture.Sequence;
        gesture.Capture(sequence, new(["A", "B"], "A")); gesture.Cancel();
        Assert.Equal(ExplorerGesturePhase.PostDropReconciliation, gesture.Phase); Assert.Null(gesture.Drop);
        Assert.False(gesture.Capture(sequence, new(["A", "B"], "A")));
    }
    [Fact]
    public void MissingHitItemNeverAssignsTheFocusedOrSelectedItems()
    {
        var gesture = new ExplorerGesture(); gesture.Begin(new(0, 0), false); gesture.StartDrag();
        Assert.False(gesture.Capture(gesture.Sequence, new(["A", "B"], null)));
        gesture.Release(new(150, 170), true); Assert.Null(gesture.Drop);
    }
    [Fact]
    public void NativeExplorerCaptureIsBlockedButOurFenceGestureCanPresentLiveIcons()
    {
        var gesture = new ExplorerGesture();
        Assert.True(gesture.BlocksFencePresentation(false, true));
        Assert.False(gesture.BlocksFencePresentation(true, true));
        gesture.Begin(new(0, 0), true);
        Assert.True(gesture.BlocksFencePresentation(true, true));
    }
    [Theory]
    [InlineData(1)] [InlineData(1.25)] [InlineData(1.5)]
    public void WholeGroupUsesPointerTargetEvenWhenEveryFinalItemIsOutside(double scale)
    {
        var fence = FenceAt();
        var result = Assert.Single(FenceIconLayout.ApplyGroupDrop([fence], Drop(["A", "B", "C", "D"], new(160, 100 + 60 * scale)),
            [Icon("A", 800, 800), Icon("B", 900, 800), Icon("C", 1000, 800), Icon("D", 1100, 800)], Metrics, _ => scale));
        Assert.Equal(["A", "B", "C", "D"], result.Icons.Select(i => i.StableId));
        for (var i = 0; i < 4; i++) Assert.Equal(FenceIconLayout.GridOffset(i, result.Width, Metrics, scale), new(result.Icons[i].OffsetX, result.Icons[i].OffsetY));
    }
    [Theory]
    [InlineData(1)] [InlineData(1.5)]
    public void NarrowFenceAcceptsWholeSpreadOutGroupAndGrowsForAllRows(double scale)
    {
        var ids = new[] { "A", "B", "C", "D", "E", "F" };
        var fence = FenceAt().WithBounds(100, 100, FenceIconLayout.MinimumWidth(Metrics, scale), 180);
        var gesture = new ExplorerGesture(); gesture.Begin(new(20, 20), false);
        gesture.Capture(gesture.Sequence, new(ids, "C")); gesture.StartDrag();
        gesture.Release(new(fence.X + 20 * scale, fence.Y + 70 * scale), true);
        var arranged = Assert.Single(FenceIconLayout.ApplyGroupDrop([fence], gesture.Drop!,
            ids.Select((id, index) => Icon(id, 1000 + index * 150, 900)).ToArray(), Metrics, _ => scale));
        Assert.Equal(ids, arranged.Icons.Select(i => i.StableId));
        Assert.True(arranged.Height > fence.Height);
        foreach (var icon in arranged.Icons)
        {
            Assert.True(icon.OffsetX + Metrics.ItemWidth <= arranged.Width);
            Assert.True(icon.OffsetY + Metrics.ItemHeight <= arranged.Height);
        }
        var outside = FenceIconLayout.ApplyGroupDrop([arranged], Drop(ids, new(2400, 1300)),
            ids.Select((id, index) => Icon(id, 2400 + index * 150, 1300)).ToArray(), Metrics, _ => scale);
        Assert.Empty(Assert.Single(outside).Icons);
    }
    [Fact]
    public void MultiDragOutRemovesAllDraggedAssignmentsButPreservesOtherItems()
    {
        var fence = FenceAt().WithIcons([new("A", "Icon", 20, 60), new("B", "Icon", 120, 60), new("C", "Icon", 20, 170)]);
        var result = Assert.Single(FenceIconLayout.ApplyGroupDrop([fence], Drop(["A", "B"], new(950, 500)),
            [Icon("A", 150, 170), Icon("B", 250, 170), Icon("C", 150, 280)], Metrics, _ => 1));
        Assert.Equal("C", Assert.Single(result.Icons).StableId);
    }
    [Fact]
    public void GroupTransfersBetweenFencesAsOneAssignmentAndPreservesExistingTargetOrder()
    {
        var first = FenceAt().WithIcons([new("A", "Icon", 20, 60), new("B", "Icon", 120, 60)]);
        var second = FenceAt(600).WithIcons([new("C", "Icon", 20, 60)]);
        var result = FenceIconLayout.ApplyGroupDrop([first, second], Drop(["A", "B"], new(650, 170)),
            [Icon("A", 150, 170), Icon("B", 250, 170), Icon("C", 650, 170)], Metrics, _ => 1);
        Assert.Empty(result[0].Icons); Assert.Equal(["C", "A", "B"], result[1].Icons.Select(i => i.StableId));
    }
    [Fact]
    public void ExternalDropDoesNotUnassignItemsCopiedToAnotherApplication()
    {
        var fence = FenceAt().WithIcons([new("A", "Icon", 20, 60)]);
        Assert.Same(fence, FenceIconLayout.ApplyGroupDrop([fence], Drop(["A"], desktop: false), [Icon("A", 150, 170)], Metrics, _ => 1)[0]);
    }
    [Fact]
    public void LastOverlappingFenceWinsAndCollapsedFencesRejectTheDrop()
    {
        var first = FenceAt(); var second = FenceAt();
        var result = FenceIconLayout.ApplyGroupDrop([first, second], Drop(["A"]), [Icon("A", 900, 900)], Metrics, _ => 1);
        Assert.Empty(result[0].Icons); Assert.Single(result[1].Icons);
        result = FenceIconLayout.ApplyGroupDrop([first.ToggleCollapsed()], Drop(["A"]), [Icon("A", 900, 900)], Metrics, _ => 1);
        Assert.Empty(result[0].Icons);
    }
    [Fact]
    public void MissingDraggedItemCannotAssignARemainingSelectionAfterACancelledMove()
    {
        var fence = FenceAt();
        Assert.Same(fence, FenceIconLayout.ApplyGroupDrop([fence], Drop(["A", "B"]), [Icon("B", 150, 170)], Metrics, _ => 1)[0]);
    }
    [Fact]
    public void DropPaddingBelongsToFenceEvenWhenItIsOutsideIconGridInset()
    {
        var fence = FenceAt();
        Assert.Same(fence, FenceIconLayout.DropTarget([fence], new(101, 140), _ => 1));
        Assert.Null(FenceIconLayout.DropTarget([fence], new(150, 120), _ => 1));
        Assert.Null(FenceIconLayout.DropTarget([fence.ToggleCollapsed()], new(101, 140), _ => 1));
    }
    [Fact]
    public void AlreadyPositionedAndUnassignedItemsAreNotRepositionedAgain()
    {
        var icons = new[] { Icon("A", 150, 170), Icon("B", 250, 170), Icon("C", 900, 900) };
        var targets = FenceIconLayout.ChangedTargets([new("A", new(150, 170)), new("B", new(350, 170)), new("missing", new(0, 0))], icons);
        Assert.Equal("B", Assert.Single(targets).StableId);
    }
}
