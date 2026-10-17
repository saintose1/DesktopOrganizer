using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Tests;

public sealed class FenceMembershipTests
{
    private static Fence Group(params string[] ids) => FenceIconLayout.Arrange(
        new Fence(Guid.NewGuid(), "Work", 100, 100, 350, 350,
            icons: ids.Select(id => new DesktopIconAssignment(id, id, 0, 0))), IconGridMetrics.Default, 1);
    private static DesktopIcon Icon(string id, string? name = null) =>
        new(0, id, name ?? id, new(20, 20), new(20, 20), new(20, 20, 90, 100));
    private static DesktopIconSnapshot Snapshot(params string[] ids) =>
        new(true, true, ids.Select(id => Icon(id)).ToArray(), IconGridMetrics.Default);
    private static HashSet<string> Missing(params string[] ids) => new(ids, FenceIconLayout.IdentityComparer);
    private static Dictionary<string, string> Aliases(params (string Before, string After)[] pairs) =>
        pairs.ToDictionary(pair => pair.Before, pair => pair.After, FenceIconLayout.IdentityComparer);

    [Fact]
    public void RemovingMiddleItemCompactsOnlyItsFenceAndPreservesOriginalSnapshot()
    {
        var affected = Group("A", "B", "C", "D");
        var untouched = Group("E", "F").ToggleCollapsed();

        var result = FenceIconLayout.ReconcileMembership([affected, untouched], Snapshot("A", "C", "D", "E", "F"),
            Missing("B"), Aliases(), _ => 1);

        Assert.Equal(["A", "C", "D"], result[0].Icons.Select(icon => icon.StableId));
        Assert.Equal(affected.Icons[1].OffsetX, result[0].Icons[1].OffsetX);
        Assert.Equal(affected.Icons[1].OffsetY, result[0].Icons[1].OffsetY);
        Assert.Equal(affected.Icons[2].OffsetX, result[0].Icons[2].OffsetX);
        Assert.Equal(affected.Icons[2].OffsetY, result[0].Icons[2].OffsetY);
        Assert.Same(untouched, result[1]);
        Assert.Equal(["A", "B", "C", "D"], affected.Icons.Select(icon => icon.StableId));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "Explorer membership changed during enumeration")]
    public void UnhealthySnapshotNeverPrunesAssignments(bool connected, string? error)
    {
        IReadOnlyList<Fence> original = [Group("A", "B")];
        var snapshot = Snapshot("A") with { Connected = connected, Error = error };

        var result = FenceIconLayout.ReconcileMembership(original, snapshot, Missing("B"), Aliases(), _ => 1);

        Assert.Same(original, result);
        Assert.Equal(2, result[0].Icons.Length);
    }

    [Fact]
    public void SingleMissingObservationWithoutConfirmationDoesNotRemoveItem()
    {
        var original = Group("A", "B");
        var result = FenceIconLayout.ReconcileMembership([original], Snapshot("A"), Missing(), Aliases(), _ => 1);
        Assert.Same(original, Assert.Single(result));
    }

    [Fact]
    public void ItemRestoredBeforeCommitWinsOverEarlierMissingConfirmation()
    {
        var original = Group("A", "B");
        var result = FenceIconLayout.ReconcileMembership([original], Snapshot("A", "b"), Missing("B"), Aliases(), _ => 1);
        Assert.Same(original, Assert.Single(result));
    }

    [Fact]
    public void RenamePreservesOwnershipOrderOffsetsAppearanceAndCollapsedHeight()
    {
        var original = Group("A", "B").ToggleCollapsed();
        var snapshot = Snapshot("B", "C") with { Icons = [Icon("B"), Icon("C", "Renamed shortcut")] };

        var result = Assert.Single(FenceIconLayout.ReconcileMembership([original], snapshot, Missing("A"),
            Aliases(("A", "C")), _ => 1.5));

        Assert.Equal(["C", "B"], result.Icons.Select(icon => icon.StableId));
        Assert.Equal("Renamed shortcut", result.Icons[0].DisplayName);
        Assert.Equal(original.Icons[0].OffsetX, result.Icons[0].OffsetX);
        Assert.Equal(original.Icons[0].OffsetY, result.Icons[0].OffsetY);
        Assert.Equal(original.X, result.X); Assert.Equal(original.Y, result.Y);
        Assert.Equal(original.Width, result.Width); Assert.Equal(original.Height, result.Height);
        Assert.True(result.IsCollapsed);
        Assert.Same(original.Appearance, result.Appearance);
        Assert.Equal("A", original.Icons[0].StableId);
    }

    [Fact]
    public void ConflictingRenameNeverStealsAnItemAssignedToAnotherFence()
    {
        var first = Group("A"); var second = Group("B");
        var result = FenceIconLayout.ReconcileMembership([first, second], Snapshot("B"), Missing("A"),
            Aliases(("A", "B")), _ => 1);
        Assert.Empty(result[0].Icons);
        Assert.Same(second, result[1]);
        Assert.Single(result.SelectMany(fence => fence.Icons));
    }

    [Fact]
    public void UnrelatedOrRestoredNewItemIsNotAutomaticallyAssigned()
    {
        var original = Group("A");
        var result = FenceIconLayout.ReconcileMembership([original], Snapshot("A", "Restored"), Missing(), Aliases(), _ => 1);
        Assert.Same(original, Assert.Single(result));
        Assert.Equal("A", Assert.Single(result[0].Icons).StableId);
    }

    [Fact]
    public void RapidRenameChainResolvesToFinalExplorerIdentity()
    {
        var original = Group("A", "Other");
        var result = Assert.Single(FenceIconLayout.ReconcileMembership([original], Snapshot("C", "Other"), Missing("A"),
            Aliases(("A", "B"), ("B", "C")), _ => 1));
        Assert.Equal(["C", "Other"], result.Icons.Select(icon => icon.StableId));
        Assert.Equal(original.Icons[0].OffsetX, result.Icons[0].OffsetX);
        Assert.Equal(original.Icons[0].OffsetY, result.Icons[0].OffsetY);
    }

    [Fact]
    public void CyclicRenameNotificationsCannotDeleteAnAssignment()
    {
        var original = Group("A");
        var result = FenceIconLayout.ReconcileMembership([original], Snapshot(), Missing("A"),
            Aliases(("A", "B"), ("B", "A")), _ => 1);
        Assert.Same(original, Assert.Single(result));
    }

    [Theory]
    [InlineData("Existing-user-shortcut.lnk")]
    [InlineData("DesktopOrganizer-probe-invalid.log")]
    public void SmokeNotificationRefusesOrdinaryUserItemsBeforeCallingShell(string name)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name);
        Assert.Throws<ArgumentException>(() => DesktopOrganizer.Infrastructure.Interop.DesktopDiagnostics.NotifySmokeItemChange(path));
    }

    [Fact]
    public void SmokeNotificationRefusesFixtureNamesOutsideCurrentUserDesktop()
    {
        var path = Path.Combine(Path.GetTempPath(), "DesktopOrganizer-probe-outside.txt");
        Assert.Throws<ArgumentException>(() => DesktopOrganizer.Infrastructure.Interop.DesktopDiagnostics.NotifySmokeItemChange(path));
    }
}
