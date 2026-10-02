using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Tests;

public sealed class FenceInteractionGeometryTests
{
    private static readonly Fence Original = new(Guid.NewGuid(), "Work", 120, 80, 450, 350);
    private static readonly DesktopArea[] Screens = [new(0, 0, 1920, 1040)];

    [Fact]
    public void HeaderActionsHaveIndependentSlotsAndLeaveFreeDragArea()
    {
        var header = new FenceHeaderLayout(450, 1.5, [FenceHeaderAction.ToggleCollapse, FenceHeaderAction.ToggleCollapse]);
        Assert.Equal(2, header.Actions.Count);
        var first = header.Actions[0];
        var second = header.Actions[1];
        Assert.Equal(second.X + second.Width, first.X);
        Assert.Null(header.ActionAt(40, 27));
        Assert.Equal(FenceHeaderAction.ToggleCollapse, header.ActionAt(first.X + 10, 27));
        Assert.Equal(FenceHeaderAction.ToggleCollapse, header.ActionAt(second.X + 10, 27));
        Assert.True(header.TitleWidth < second.X);
    }

    [Theory]
    [InlineData(2, 100, FenceHit.Left)]
    [InlineData(448, 100, FenceHit.Right)]
    [InlineData(100, 2, FenceHit.Top)]
    [InlineData(100, 348, FenceHit.Bottom)]
    [InlineData(2, 2, FenceHit.Left | FenceHit.Top)]
    [InlineData(448, 2, FenceHit.Right | FenceHit.Top)]
    [InlineData(2, 348, FenceHit.Left | FenceHit.Bottom)]
    [InlineData(448, 348, FenceHit.Right | FenceHit.Bottom)]
    [InlineData(100, 18, FenceHit.Header)]
    [InlineData(425, 18, FenceHit.Collapse)]
    [InlineData(100, 100, FenceHit.None)]
    public void HitTestSeparatesHeaderButtonBodyAndAllResizeHandles(double x, double y, FenceHit expected)
        => Assert.Equal(expected, FenceInteractionGeometry.HitTest(Original, x, y, 1));

    [Theory]
    [InlineData(FenceHit.Left, 30, 0, 150, 80, 420, 350)]
    [InlineData(FenceHit.Right, 30, 0, 120, 80, 480, 350)]
    [InlineData(FenceHit.Top, 0, 20, 120, 100, 450, 330)]
    [InlineData(FenceHit.Bottom, 0, 20, 120, 80, 450, 370)]
    [InlineData(FenceHit.Left | FenceHit.Top, 30, 20, 150, 100, 420, 330)]
    [InlineData(FenceHit.Right | FenceHit.Top, 30, 20, 120, 100, 480, 330)]
    [InlineData(FenceHit.Left | FenceHit.Bottom, 30, 20, 150, 80, 420, 370)]
    [InlineData(FenceHit.Right | FenceHit.Bottom, 30, 20, 120, 80, 480, 370)]
    public void ResizingKeepsOppositeEdgesFixed(FenceHit hit, double dx, double dy, double x, double y, double w, double h)
        => Assert.Equal(Original.WithBounds(x, y, w, h), FenceInteractionGeometry.MoveOrResize(Original, hit, dx, dy, 1, Screens));

    [Fact]
    public void DragUsesAbsoluteDeltaFromStartAndPreservesFractionalOffset()
    {
        var start = Original.WithBounds(120.5, 80.25, 450, 350);
        Assert.Equal(start, FenceInteractionGeometry.MoveOrResize(start, FenceHit.Header, 0, 0, 1, Screens));
        Assert.Equal(start.WithBounds(145.5, 115.25, 450, 350), FenceInteractionGeometry.MoveOrResize(start, FenceHit.Header, 25, 35, 1, Screens));
    }

    [Theory]
    [InlineData(1)] [InlineData(1.25)] [InlineData(1.5)]
    public void MinimumSizeAndHeaderHitAreasScaleWithDpi(double scale)
    {
        var resized = FenceInteractionGeometry.MoveOrResize(Original, FenceHit.Left | FenceHit.Top, 900, 900, scale, Screens);
        Assert.Equal(180 * scale, resized.Width);
        Assert.Equal(90 * scale, resized.Height);
        Assert.Equal(Original.X + Original.Width, resized.X + resized.Width);
        Assert.Equal(Original.Y + Original.Height, resized.Y + resized.Height);
        Assert.Equal(FenceHit.Collapse, FenceInteractionGeometry.HitTest(Original, Original.Width - 20 * scale, 18 * scale, scale));
    }

    [Fact]
    public void MonitorsWithNegativeOriginsAndGapsKeepHeaderRecoverable()
    {
        DesktopArea[] screens = [new(-1920, 0, 1920, 1040), new(0, 400, 1920, 1040)];
        var onLeft = FenceInteractionGeometry.MoveOrResize(Original, FenceHit.Header, -1500, 0, 1, screens);
        Assert.Equal(-1380, onLeft.X);
        var inGap = FenceInteractionGeometry.KeepHeaderAccessible(Original.WithBounds(500, 100, 450, 350), 1, screens);
        Assert.Equal(400, inGap.Y);
        var farAway = FenceInteractionGeometry.MoveOrResize(Original, FenceHit.Header, 10000, 10000, 1, screens);
        Assert.Equal(1820, farAway.X);
        Assert.Equal(1404, farAway.Y);
    }

    [Fact]
    public void CollapsePreservesIdAndNormalHeightAndOnlyChangesVisibleLayout()
    {
        var collapsed = Original.ToggleCollapsed();
        Assert.Equal(Original.Id, collapsed.Id);
        Assert.Equal(350, collapsed.Height);
        Assert.True(collapsed.IsCollapsed);
        Assert.Equal(36, FenceSurfaceLayout.Clip(collapsed, Screens[0])!.Value.Height);
        Assert.Equal(45, FenceSurfaceLayout.Clip(collapsed, Screens[0], 1.25)!.Value.Height);
        Assert.Equal(Original, collapsed.ToggleCollapsed());
        var wider = FenceInteractionGeometry.MoveOrResize(collapsed, FenceHit.Right | FenceHit.Bottom, 20, 900, 1, Screens);
        Assert.Equal(350, wider.Height);
        Assert.Equal(470, wider.Width);
    }
}
