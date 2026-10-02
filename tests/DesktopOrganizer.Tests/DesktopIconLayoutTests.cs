using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Tests;

public sealed class DesktopIconLayoutTests
{
    private static readonly IconGridMetrics Metrics = new(100, 110, 90, 100, -10, -2);
    private static Fence Group(double x = 100, double y = 100, double width = 350, double height = 350) =>
        new(Guid.NewGuid(), "Work", x, y, width, height);
    private static DesktopIcon Icon(string id, double x, double y, int index = 0) =>
        new(index, id, "Same display name", new(x, y), new(x, y), new(x - 10, y - 2, 90, 100));
    private static DesktopIconAssignment Assignment(string id) => new(id, "Same display name", 22, 46);

    [Theory]
    [InlineData(0, 0, 0, 0, 30, 40)]
    [InlineData(-1920, -200, 15, 25, -1500, -100)]
    [InlineData(0, 5, 0, 0, 33, 2)]
    public void CoordinatesRoundTripWithNegativeMonitorsAndViewOrigin(double cx, double cy, double vx, double vy, double x, double y)
    {
        var coordinates = new DesktopCoordinates(new(cx, cy), new(vx, vy));
        var point = new DesktopPoint(x, y);
        Assert.Equal(new DesktopPoint(cx + x - vx, cy + y - vy), coordinates.ToScreen(point));
        Assert.Equal(point, coordinates.ToView(coordinates.ToScreen(point)));
    }

    [Fact]
    public void MatchingUsesParsingIdentityAndIgnoresIndexAndDisplayName()
    {
        var a = Icon(@"C:\Desktop\A.lnk", 100, 100, 70);
        var b = Icon(@"C:\Desktop\B.lnk", 200, 100, 0);
        var matched = FenceIconLayout.Match([a, b]);
        Assert.Same(a, matched[@"c:\desktop\a.LNK"]);
        Assert.Same(b, matched[b.StableId]);
        Assert.False(matched.ContainsKey(a.DisplayName));
    }

    [Fact]
    public void AmbiguousIdentitiesAreSkippedInsteadOfMovingWrongIcon()
    {
        var matched = FenceIconLayout.Match([Icon("A", 100, 100), Icon("a", 200, 100), Icon("B", 300, 100)]);
        Assert.Single(matched);
        Assert.True(matched.ContainsKey("B"));
    }

    [Fact]
    public void DropAssignsAndGridKeepsFullIconBoundsInsideContent()
    {
        var fence = Group();
        var result = Assert.Single(FenceIconLayout.ApplyMovedIcons([fence], [Icon("A", 150, 150)], Metrics, _ => 1));
        var assignment = Assert.Single(result.Icons);
        Assert.Equal("A", assignment.StableId);
        var content = FenceIconLayout.ContentBounds(result, 1);
        Assert.Equal(content.X, result.X + assignment.OffsetX + Metrics.BoundsOffsetX);
        Assert.Equal(content.Y, result.Y + assignment.OffsetY + Metrics.BoundsOffsetY);
        Assert.True(result.X + assignment.OffsetX + Metrics.BoundsOffsetX + Metrics.ItemWidth <= content.X + content.Width);
        Assert.True(result.Y + assignment.OffsetY + Metrics.BoundsOffsetY + Metrics.ItemHeight <= content.Y + content.Height);
        Assert.Empty(fence.Icons);
    }

    [Fact]
    public void DroppingOutsideRemovesOnlyAssignmentAndLeavesExplorerPositionAlone()
    {
        var fence = Group().WithIcons([Assignment("A"), Assignment("B")]);
        var moved = Icon("A", 700, 700);
        var result = Assert.Single(FenceIconLayout.ApplyMovedIcons([fence], [moved], Metrics, _ => 1));
        Assert.Equal("B", Assert.Single(result.Icons).StableId);
        Assert.Equal(new DesktopPoint(700, 700), moved.Position);
    }

    [Fact]
    public void DropTransfersOwnershipBetweenFencesWithoutDuplicates()
    {
        var first = Group().WithIcons([Assignment("A")]);
        var second = Group(600, 100);
        var result = FenceIconLayout.ApplyMovedIcons([first, second], [Icon("a", 650, 150)], Metrics, _ => 1);
        Assert.Empty(result[0].Icons);
        Assert.Equal("a", Assert.Single(result[1].Icons).StableId);
    }

    [Fact]
    public void HeaderAndCollapsedFenceDoNotAcceptAssignments()
    {
        var fence = Group();
        Assert.Empty(FenceIconLayout.ApplyMovedIcons([fence], [Icon("A", 150, 80)], Metrics, _ => 1)[0].Icons);
        Assert.Empty(FenceIconLayout.ApplyMovedIcons([fence.ToggleCollapsed()], [Icon("A", 150, 150)], Metrics, _ => 1)[0].Icons);
    }

    [Fact]
    public void LastOverlappingFenceReceivesDroppedIcon()
    {
        var result = FenceIconLayout.ApplyMovedIcons([Group(), Group()], [Icon("A", 150, 150)], Metrics, _ => 1);
        Assert.Empty(result[0].Icons);
        Assert.Single(result[1].Icons);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void ResizeReflowsRowsGrowsHeightAndPreservesAssignments(double scale)
    {
        var original = Group(width: 450, height: 200).WithIcons(Enumerable.Range(0, 6).Select(i => Assignment(i.ToString())));
        var wide = FenceIconLayout.Arrange(original, Metrics, scale);
        var narrow = FenceIconLayout.Arrange(wide.WithBounds(100, 100, 180 * scale, 100), Metrics, scale);
        Assert.Equal(wide.Icons.Select(i => i.StableId), narrow.Icons.Select(i => i.StableId));
        Assert.True(narrow.Height > wide.Height);
        var last = narrow.Icons[^1];
        Assert.True(last.OffsetY + Metrics.BoundsOffsetY + Metrics.ItemHeight <= narrow.Height - 12 * scale);
        Assert.Equal(FenceIconLayout.GridOffset(5, narrow.Width, Metrics, scale), new DesktopPoint(last.OffsetX, last.OffsetY));
    }

    [Fact]
    public void LeftTopResizePreservesOppositeCornerWhenConstrainedByIcons()
    {
        var original = Group().WithIcons(Enumerable.Range(0, 5).Select(i => Assignment(i.ToString())));
        var next = FenceIconLayout.ConstrainResize(original.WithBounds(440, 440, 10, 10), original,
            FenceHit.Left | FenceHit.Top, Metrics, 1);
        Assert.Equal(original.X + original.Width, next.X + next.Width);
        Assert.Equal(original.Y + original.Height, next.Y + next.Height);
        Assert.True(next.Height >= FenceIconLayout.RequiredHeight(next.Width, 5, Metrics, 1));
    }

    [Fact]
    public void MovementAndCollapseKeepRelativeOffsetsAndExpandedHeight()
    {
        var fence = FenceIconLayout.Arrange(Group().WithIcons([Assignment("A")]), Metrics, 1);
        var moved = fence.WithBounds(-1400, -100, fence.Width, fence.Height).ToggleCollapsed();
        Assert.Equal(fence.Icons, moved.Icons);
        Assert.Equal(fence.Height, moved.Height);
        Assert.Equal(fence.WithBounds(-1400, -100, fence.Width, fence.Height), moved.ToggleCollapsed());
    }

    [Fact]
    public void InvalidOrDuplicateAssignmentsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new DesktopIconAssignment("", "A", 0, 0));
        Assert.Throws<ArgumentException>(() => new DesktopIconAssignment("A", "", 0, 0));
        Assert.Throws<ArgumentException>(() => new DesktopIconAssignment("A", "A", double.NaN, 0));
        Assert.Throws<ArgumentException>(() => Group().WithIcons([Assignment("A"), Assignment("a")]));
        Assert.Throws<ArgumentException>(() => Group().WithIcons([null!]));
    }
}
