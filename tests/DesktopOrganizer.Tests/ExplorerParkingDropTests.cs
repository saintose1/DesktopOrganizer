using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Tests;

public sealed class ExplorerParkingDropTests
{
    private static readonly DesktopArea[] Monitors = [new(-1920, 0, 1920, 1080), new(0, 0, 2560, 1600)];
    private static DesktopIcon Icon(string id, double x, double y) => new(0, id, id, new(x, y), new(x, y), new(x, y, 100, 100));
    private static Fence Group(bool collapsed, params string[] ids) => new(Guid.NewGuid(), "Group", 100, 100, 450, 350,
        collapsed, ids.Select(id => new DesktopIconAssignment(id, id, 40, 70)));
    private static DesktopIconSnapshot Snapshot(params DesktopIcon[] icons) => new(true, true, icons, IconGridMetrics.Default);

    [Fact]
    public void NativeDropClamping52ParkedItemsReturnsExactOldParkingWithoutChangingAssignments()
    {
        var ids = Enumerable.Range(0, 52).Select(i => "parked:" + i).ToArray();
        var group = Group(true, ids);
        var before = ids.Select((id, i) => Icon(id, -8192 - i * 128, -4096)).Append(Icon("ordinary", 1500, 800)).ToArray();
        var after = Snapshot(ids.Select((id, i) => Icon(id, 50 + i % 20 * 115, 5 + i / 20 * 135))
            .Append(Icon("ordinary", 1800, 900)).ToArray());

        var targets = IconPlacement.DisplacedParking([group], before, after, ["ordinary"], Monitors);

        Assert.Equal(52, targets.Count);
        Assert.All(targets, t => Assert.Equal(before.Single(i => i.StableId == t.StableId).Position, t.Position));
        Assert.DoesNotContain(targets, t => t.StableId == "ordinary");
        Assert.Equal(ids, group.Icons.Select(i => i.StableId));
    }

    [Fact]
    public void DraggedSelectionAndExpandedGroupsAreNotParkingRecoveryTargets()
    {
        var before = new[] { Icon("hidden", -8192, -4096), Icon("selected", -9000, -4096), Icon("expanded", -10000, -4096) };
        var after = Snapshot(Icon("hidden", 100, 100), Icon("selected", 300, 100), Icon("expanded", 500, 100));
        var targets = IconPlacement.DisplacedParking([Group(true, "hidden", "selected"), Group(false, "expanded")],
            before, after, ["selected"], Monitors);
        Assert.Equal("hidden", Assert.Single(targets).StableId);
    }

    [Fact]
    public void AlreadyParkedItemsAndPositionDriftOutsideScreensDoNotWritePositions()
    {
        var before = new[] { Icon("A", -8192, -4096), Icon("B", -9000, -4096) };
        Assert.Empty(IconPlacement.DisplacedParking([Group(true, "A", "B")], before,
            Snapshot(before[0], Icon("B", -10000, -4096)), [], Monitors));
    }

    [Theory]
    [InlineData(false, true, null)]
    [InlineData(true, false, null)]
    [InlineData(true, true, "Incomplete enumeration")]
    public void UnhealthySnapshotCannotTriggerParkingWrites(bool connected, bool canPosition, string? error)
    {
        var after = Snapshot(Icon("A", 100, 100)) with { Connected = connected, CanPosition = canPosition, Error = error };
        Assert.Empty(IconPlacement.DisplacedParking([Group(true, "A")], [Icon("A", -8192, -4096)], after, [], Monitors));
    }

    [Fact]
    public void AnIconPreviouslyVisibleOnNegativeCoordinateMonitorIsNotTreatedAsParked()
    {
        Assert.Empty(IconPlacement.DisplacedParking([Group(true, "A")], [Icon("A", -1500, 200)],
            Snapshot(Icon("A", 500, 200)), [], Monitors));
    }

    [Fact]
    public void MissingOrAmbiguousIdentitiesAreNeverRecovered()
    {
        var group = Group(true, "A", "B");
        Assert.Empty(IconPlacement.DisplacedParking([group], [Icon("A", -8192, -4096), Icon("B", -9000, -4096)],
            Snapshot(Icon("A", 100, 100), Icon("A", 200, 100)), [], Monitors));
    }
}
