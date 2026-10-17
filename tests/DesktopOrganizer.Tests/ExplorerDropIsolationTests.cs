using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Tests;

/// <summary>Drop ownership contracts; these tests do not reproduce native Explorer gestures.</summary>
public sealed class ExplorerDropIsolationTests
{
    private static readonly IconGridMetrics Metrics = new(100, 110, 90, 90);
    private static readonly DesktopArea[] Monitors = [new(0, 0, 1920, 1080)];
    private static Fence Group(string name, double x, bool collapsed, params string[] ids) =>
        FenceIconLayout.Arrange(new Fence(Guid.NewGuid(), name, x, 100, 300, 300, collapsed)
            .WithIcons(ids.Select(id => new DesktopIconAssignment(id, id, 0, 0))), Metrics, 1);
    private static DesktopIcon Icon(string id, double x, double y) =>
        new(0, id, id, new(x, y), new(x, y), new(x, y, 90, 90));
    private static ExplorerDrop Drop(string[] ids, DesktopPoint point) => new(ids, ids[0], point, true);

    [Fact]
    public void OrdinaryOutsideDropCannotChangeExpandedOrCollapsedAssignments()
    {
        var expanded = Group("Expanded", 100, false, "A", "B");
        var collapsed = Group("Collapsed", 600, true, "C", "D");
        var result = FenceIconLayout.ApplyGroupDrop([expanded, collapsed], Drop(["ordinary"], new(1100, 700)),
            [Icon("ordinary", 1100, 700), Icon("A", 900, 500), Icon("B", 900, 600), Icon("C", 1000, 500), Icon("D", 1000, 600)],
            Metrics, _ => 1);

        Assert.Same(expanded, result[0]);
        Assert.Same(collapsed, result[1]);
        Assert.True(result[1].IsCollapsed);
        Assert.Empty(IconPlacement.ChangedFenceTargets([expanded, collapsed], result, Monitors, Metrics));
    }

    [Fact]
    public void DroppingIntoOneFenceDoesNotAdoptUnrelatedPositionDrift()
    {
        var target = Group("Target", 100, false, "A");
        var expanded = Group("Expanded", 600, false, "B");
        var collapsed = Group("Collapsed", 1000, true, "C");
        var result = FenceIconLayout.ApplyGroupDrop([target, expanded, collapsed], Drop(["ordinary"], new(160, 180)),
            [Icon("ordinary", 900, 900), Icon("A", 130, 150), Icon("B", 160, 180), Icon("C", 200, 200)], Metrics, _ => 1);

        Assert.Equal(["A", "ordinary"], result[0].Icons.Select(i => i.StableId));
        Assert.Same(expanded, result[1]);
        Assert.Same(collapsed, result[2]);
    }

    [Fact]
    public void MultiDropOutChangesOnlyCapturedOwners()
    {
        var source = Group("Source", 100, false, "A", "B", "C");
        var unrelated = Group("Other", 600, true, "D", "E");
        var result = FenceIconLayout.ApplyGroupDrop([source, unrelated], Drop(["A", "B"], new(1500, 800)),
            [Icon("A", 1500, 800), Icon("B", 1600, 800), Icon("C", 150, 200), Icon("D", 1400, 500), Icon("E", 1400, 600)],
            Metrics, _ => 1);

        Assert.Equal("C", Assert.Single(result[0].Icons).StableId);
        Assert.Same(unrelated, result[1]);
    }

    [Fact]
    public void IncompleteSnapshotWithoutDraggedAnchorCannotMutateAnyFence()
    {
        var first = Group("First", 100, false, "A", "B");
        var second = Group("Second", 600, true, "C");
        var result = FenceIconLayout.ApplyGroupDrop([first, second], Drop(["ordinary", "B"], new(160, 180)),
            [Icon("B", 900, 900)], Metrics, _ => 1);

        Assert.Same(first, result[0]);
        Assert.Same(second, result[1]);
    }

    [Fact]
    public void PreviousSelectionNeverPullsFenceIconsIntoAnOrdinarySingleDrag()
    {
        var first = Group("First", 100, false, "A", "B");
        var second = Group("Second", 600, true, "C");
        var gesture = new ExplorerGesture();
        gesture.Begin(new(950, 600), false);
        gesture.Capture(gesture.Sequence, new(["A", "B", "C"], "ordinary"));
        gesture.StartDrag();
        gesture.Release(new(1100, 700), true);

        Assert.Equal(["ordinary"], gesture.Drop!.DraggedIds);
        var result = FenceIconLayout.ApplyGroupDrop([first, second], gesture.Drop,
            [Icon("ordinary", 1100, 700), Icon("A", 130, 150), Icon("B", 230, 150), Icon("C", -4096, -4096)], Metrics, _ => 1);
        Assert.Same(first, result[0]);
        Assert.Same(second, result[1]);
    }

    [Fact]
    public void DropTargetsExcludeOtherFenceIconsEvenWhenTheirSnapshotPositionsDrift()
    {
        var target = Group("Target", 100, false, "A");
        var expanded = Group("Expanded", 600, false, "B");
        var collapsed = Group("Collapsed", 1000, true, "C");
        var before = new[] { target, expanded, collapsed };
        var snapshot = new[] { Icon("ordinary", 900, 900), Icon("A", 800, 800), Icon("B", 160, 180), Icon("C", 200, 200) };
        var after = FenceIconLayout.ApplyGroupDrop(before, Drop(["ordinary"], new(160, 180)), snapshot, Metrics, _ => 1);
        var targets = FenceIconLayout.ChangedTargets(IconPlacement.ChangedFenceTargets(before, after, Monitors, Metrics), snapshot);

        Assert.Equal(["A", "ordinary"], targets.Select(i => i.StableId));
        Assert.Same(expanded, after[1]);
        Assert.Same(collapsed, after[2]);
        Assert.True(after[2].IsCollapsed);
    }

    [Fact]
    public void GroupTransferPositionsOnlySourceAndTargetContents()
    {
        var source = Group("Source", 100, false, "A", "B", "C");
        var target = Group("Target", 600, false, "D");
        var unrelated = Group("Other", 1000, true, "E", "F");
        var before = new[] { source, target, unrelated };
        var after = FenceIconLayout.ApplyGroupDrop(before, Drop(["A", "B"], new(660, 180)),
            [Icon("A", 660, 180), Icon("B", 1600, 800), Icon("C", 150, 200), Icon("D", 650, 160), Icon("E", 1400, 500), Icon("F", 1400, 600)],
            Metrics, _ => 1);
        var targets = IconPlacement.ChangedFenceTargets(before, after, Monitors, Metrics);

        Assert.Equal(["C", "D", "A", "B"], targets.Select(i => i.StableId));
        Assert.Same(unrelated, after[2]);
        Assert.Equal(unrelated.Icons, after[2].Icons);
    }

    [Fact]
    public void RemovingOneGroupsParkedItemDoesNotRepositionAnotherCollapsedGroup()
    {
        var changed = Group("Changed", 100, true, "A");
        var unrelated = Group("Other", 600, true, "B", "C");
        var before = new[] { changed, unrelated };
        var after = new[] { changed.WithIcons([]), unrelated };

        // Removing a global parking slot must not turn another group's slot shift into a gesture write.
        Assert.Empty(IconPlacement.ChangedFenceTargets(before, after, Monitors, Metrics));
        Assert.Same(unrelated, after[1]);
    }

    [Fact]
    public void AReadOnlyDriftedSnapshotCannotCreateAnyDropPositionTargets()
    {
        var expanded = Group("Expanded", 100, false, "A", "B");
        var collapsed = Group("Collapsed", 600, true, "C", "D");
        var fences = new[] { expanded, collapsed };
        var targets = FenceIconLayout.ChangedTargets(IconPlacement.ChangedFenceTargets(fences, fences, Monitors, Metrics),
            [Icon("A", 900, 600), Icon("B", 1000, 600), Icon("C", 1100, 600), Icon("D", 1200, 600)]);

        Assert.Empty(targets);
        Assert.Equal(["A", "B"], expanded.Icons.Select(i => i.StableId));
        Assert.Equal(["C", "D"], collapsed.Icons.Select(i => i.StableId));
        Assert.True(collapsed.IsCollapsed);
    }
}
