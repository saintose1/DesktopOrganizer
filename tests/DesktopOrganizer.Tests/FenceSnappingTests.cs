using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Tests;

public sealed class FenceSnappingTests
{
    private static readonly DesktopArea[] Monitor = [new(0, 0, 2560, 1528)];
    private static Fence At(double x, double y) => new(Guid.NewGuid(), "Moving", x, y, 300, 200);

    [Fact]
    public void SnapsToTheRightEdgeOfANeighbourAndAlignsTops()
    {
        var snapped = FenceSnapping.Snap(At(509, 7), 200, [new DesktopBounds(200, 0, 300, 36)], Monitor, 1);
        Assert.Equal(500, snapped.X);
        Assert.Equal(0, snapped.Y);
    }

    [Fact]
    public void SnapsToScreenEdges()
    {
        var snapped = FenceSnapping.Snap(At(2255, 1320), 200, [], Monitor, 1);
        Assert.Equal(2260, snapped.X); // right edge flush with the screen
        Assert.Equal(1328, snapped.Y); // bottom edge flush with the work area
    }

    [Fact]
    public void FarFromEveryEdgeNothingChanges()
    {
        var fence = At(800, 600);
        Assert.Same(fence, FenceSnapping.Snap(fence, 200, [new DesktopBounds(100, 100, 300, 200)], Monitor, 1));
    }

    [Fact]
    public void ThresholdScalesWithDpi()
    {
        var bounds = new DesktopBounds(0, 900, 300, 36);
        Assert.Equal(318.0, FenceSnapping.Snap(At(318, 900), 200, [bounds], Monitor, 1).X); // 18 px > 12 px: stays free
        Assert.Equal(300.0, FenceSnapping.Snap(At(318, 900), 200, [bounds], Monitor, 2).X); // 18 px < 24 px at 200%
    }
}
