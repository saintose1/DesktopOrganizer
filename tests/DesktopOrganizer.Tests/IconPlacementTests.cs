using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Tests;

public sealed class IconPlacementTests
{
    private static Fence Group(bool collapsed) => new Fence(Guid.NewGuid(), "Work", -500, 100, 450, 350, collapsed,
        [new("A", "A", 40, 70), new("B", "B", 140, 70)]);
    private static readonly DesktopArea[] Monitors = [new(-1920, -300, 1920, 1080), new(0, 0, 2560, 1600)];
    [Fact]
    public void CollapseParksOutsideEveryMonitorAndPreservesOffsets()
    {
        var group = Group(true); var offsets = group.Icons.ToArray();
        var targets = IconPlacement.Targets([group], Monitors, IconGridMetrics.Default);
        Assert.Equal(2, targets.Count); Assert.NotEqual(targets[0].Position, targets[1].Position);
        foreach (var target in targets)
            foreach (var monitor in Monitors) Assert.True(target.Position.X + 100 < monitor.X && target.Position.Y + 100 < monitor.Y);
        Assert.Equal(offsets, group.Icons);
    }
    [Fact]
    public void ExpandRestoresExactNormalPositionsAfterCollapsedDrag()
    {
        var group = Group(true).WithBounds(-200, 300, 450, 350).ToggleCollapsed();
        Assert.Equal(new DesktopPoint(-160, 370), IconPlacement.Targets([group], Monitors, IconGridMetrics.Default)[0].Position);
        Assert.Equal(350, group.Height);
    }
    [Fact]
    public void RestartProducesSameParkedPositions()
    {
        var group = Group(true);
        Assert.Equal(IconPlacement.Targets([group], Monitors, IconGridMetrics.Default), IconPlacement.Targets([group], Monitors, IconGridMetrics.Default));
    }
    [Fact]
    public void MovingCollapsedHeaderDoesNotMoveParkedIcons()
    {
        var group = Group(true);
        Assert.Equal(IconPlacement.Targets([group], Monitors, IconGridMetrics.Default),
            IconPlacement.Targets([group.WithBounds(100, 200, 450, 350)], Monitors, IconGridMetrics.Default));
        Assert.NotEqual(IconPlacement.Recovery([group])[0], IconPlacement.Recovery([group.WithBounds(100, 200, 450, 350)])[0]);
    }
    [Fact]
    public void RecoveryContainsOnlyCollapsedIconsAtTheirNormalPositions()
    {
        Assert.Empty(IconPlacement.Recovery([Group(false)]));
        Assert.Equal(new DesktopPoint(-460, 170), IconPlacement.Recovery([Group(true)])[0].Position);
    }
    [Fact]
    public void ImmediateCollapseRecoveryIsArmedWhileTheFenceIsStillExpanded()
    {
        var expanded = Group(false);
        Assert.Equal(IconPlacement.Recovery([expanded.ToggleCollapsed()]), IconPlacement.ReturnPositions([expanded]));
        Assert.Equal(IconPlacement.ReturnPositions([expanded]), IconPlacement.ReturnPositions([expanded.ToggleCollapsed()]));
    }
    [Fact]
    public void ReturnPositionsReflectFinishedGeometryAndKeepRelativeOffsets()
    {
        var fence = Group(false).WithBounds(800, 400, 600, 500);
        Assert.Equal(new DesktopPoint(840, 470), IconPlacement.ReturnPositions([fence])[0].Position);
        Assert.Equal(new DesktopPoint(940, 470), IconPlacement.ReturnPositions([fence])[1].Position);
        Assert.Equal(40, fence.Icons[0].OffsetX);
    }
    [Fact]
    public void ParkingRequiresAvailableMonitors() => Assert.Throws<ArgumentException>(() => IconPlacement.Parking([], 0, IconGridMetrics.Default));
    [Fact]
    public void ParkingDoesNotOverflowAtLargeSlot() => Assert.InRange(IconPlacement.Parking(Monitors, 100000, IconGridMetrics.Default).X, int.MinValue, int.MaxValue);
    [Theory]
    [InlineData(false, 100, false)]
    [InlineData(true, 101, true)]
    [InlineData(true, 104, false)]
    public void ReadbackRequiresConnectionAndActualTargetPosition(bool connected, double x, bool expected)
    {
        var snapshot = new DesktopIconSnapshot(connected, true, [new(0, "A", "A", new(x, 100), new(x, 100), new(x, 100, 90, 100))], IconGridMetrics.Default);
        Assert.Equal(expected, IconPlacement.AtTargets(snapshot, [new("a", new(100, 100))]));
    }
}
