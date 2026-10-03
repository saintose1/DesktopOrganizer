using DesktopOrganizer.App.Desktop;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Tests;

public sealed class FenceIconAnimationTests
{
    private static readonly DesktopArea[] Monitors = [new(-1920, 0, 1920, 1080), new(0, 0, 2560, 1600)];
    private static FenceIconAnimationPlan Plan(Fence target, IReadOnlyList<DesktopIcon> snapshot,
        IReadOnlyList<DesktopIconTarget> final, bool startIsCollapsed) => new(target, snapshot, final, Monitors, startIsCollapsed,
            IconPlacement.Targets([target.IsCollapsed ? target : target.ToggleCollapsed()], Monitors, IconGridMetrics.Default), 54);
    private static (Fence Fence, DesktopIcon[] Icons) Seed(int count, bool collapsed)
    {
        var assignments = Enumerable.Range(0, count).Select(i => new DesktopIconAssignment("shell:" + i, "Icon " + i,
            40 + i % 5 * 110, 68 + i / 5 * 110)).ToArray();
        var fence = new Fence(Guid.NewGuid(), "Animation", -700, 100, 750, 650, collapsed, assignments);
        var icons = assignments.Select((a, i) =>
        {
            var p = collapsed ? IconPlacement.Parking(Monitors, i, IconGridMetrics.Default) : IconPlacement.Normal(fence, a);
            return new DesktopIcon(i, a.StableId, a.DisplayName, p, p, new(p.X - 30, p.Y - 2, 100, 80));
        }).ToArray();
        return (fence, icons);
    }
    [Theory]
    [InlineData(1)][InlineData(5)][InlineData(12)]
    public void ExpandUsesLocalHiddenStartCommonProgressAndExactFinalTargets(int count)
    {
        var (collapsed, snapshot) = Seed(count, true); var expanded = collapsed.ToggleCollapsed();
        var normal = IconPlacement.Targets([expanded], Monitors, IconGridMetrics.Default);
        var plan = Plan(expanded, snapshot, normal, startIsCollapsed: true);
        var buffer = new DesktopIconTarget[count]; plan.Fill(0, buffer);
        Assert.All(buffer, t => Assert.InRange(t.Position.Y, expanded.Y, expanded.Y + expanded.Height));
        Assert.DoesNotContain(buffer, t => t.Position == snapshot.Single(i => i.StableId == t.StableId).Position);
        var starts = buffer.ToArray();
        var easing = FenceTransitionAnimator.EasedProgress(0.25, expanding: true);
        plan.Fill(easing, buffer);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(normal[i].Position.X, buffer[i].Position.X);
            Assert.Equal(Math.Round(starts[i].Position.Y + (normal[i].Position.Y - starts[i].Position.Y) * easing), buffer[i].Position.Y);
        }
        plan.Fill(1, buffer); Assert.Equal(normal, buffer);
        Assert.True(collapsed.IsCollapsed); Assert.Equal(650, collapsed.Height);
        Assert.Equal(collapsed.Icons, expanded.Icons);
    }
    [Theory]
    [InlineData(1)][InlineData(5)][InlineData(12)]
    public void CollapseMovesLocallyUntilInvisibleFinalParkingAndKeepsOffsets(int count)
    {
        var (expanded, snapshot) = Seed(count, false); var collapsed = expanded.ToggleCollapsed();
        var parked = IconPlacement.Targets([collapsed], Monitors, IconGridMetrics.Default);
        var plan = Plan(collapsed, snapshot, parked, startIsCollapsed: false);
        var buffer = new DesktopIconTarget[count];
        plan.Fill(0, buffer); Assert.Equal(snapshot.Select(i => new DesktopIconTarget(i.StableId, i.Position)), buffer);
        plan.Fill(0.5, buffer);
        Assert.All(buffer, t => Assert.InRange(t.Position.Y, expanded.Y, expanded.Y + expanded.Height));
        for (var i = 0; i < count; i++) Assert.True(buffer[i].Position.Y < snapshot[i].Position.Y);
        plan.Fill(0.999, buffer); Assert.All(buffer, t => Assert.True(t.Position.Y >= expanded.Y));
        plan.Fill(1, buffer); Assert.Equal(parked, buffer);
        Assert.All(parked, t => Assert.DoesNotContain(Monitors, m => new DesktopBounds(m.X, m.Y, m.Width, m.Height).Contains(t.Position)));
        Assert.Equal(expanded.Icons, collapsed.Icons);
    }
    [Fact]
    public void FinalFrameIncludesOtherCollapsedParkingSlotsAndResumeUsesActualVisiblePositions()
    {
        var (expanded, snapshot) = Seed(5, false);
        var other = new DesktopIconTarget("other:item", IconPlacement.Parking(Monitors, 7, IconGridMetrics.Default));
        var final = IconPlacement.Targets([expanded], Monitors, IconGridMetrics.Default).Append(other).ToArray();
        var partial = snapshot.Select(i => i with { Position = i.Position with { Y = i.Position.Y - 20 } }).ToArray();
        var plan = Plan(expanded, partial, final, startIsCollapsed: false);
        var buffer = new DesktopIconTarget[6];
        Assert.Equal(5, plan.Fill(0, buffer)); Assert.Equal(partial[0].Position, buffer[0].Position);
        Assert.Equal(6, plan.Fill(1, buffer)); Assert.Equal(final, buffer);
        Assert.Throws<ArgumentException>(() => plan.Fill(double.NaN, buffer));
        Assert.Throws<ArgumentException>(() => plan.Fill(1, new DesktopIconTarget[1]));
    }
    [Fact]
    public void RoundedEasingDoesNotParkBeforeClockFinalFrame()
    {
        var (expanded, snapshot) = Seed(1, false);
        var final = IconPlacement.Targets([expanded.ToggleCollapsed()], Monitors, IconGridMetrics.Default)
            .Append(new DesktopIconTarget("other:item", new(-2200, -300))).ToArray();
        var plan = Plan(expanded.ToggleCollapsed(), snapshot, final, false);
        var group = new DesktopIconTarget[1];
        Assert.Equal(1, plan.Fill(1, finalFrame: false, group));
        Assert.True(group[0].Position.Y >= expanded.Y);
        var all = new DesktopIconTarget[2];
        Assert.Equal(2, plan.Fill(1, finalFrame: true, all)); Assert.Equal(final, all);
    }
    [Theory]
    [InlineData(1, false)][InlineData(5, false)][InlineData(12, false)]
    [InlineData(1, true)][InlineData(5, true)][InlineData(12, true)]
    public void VisibilityFollowsWholeGlyphBoundsAndDoesNotReappearAfterCollapse(int count, bool collapsing)
    {
        var (start, snapshot) = Seed(count, collapsed: !collapsing);
        var target = start.ToggleCollapsed();
        var final = IconPlacement.Targets([target], Monitors, IconGridMetrics.Default);
        var parked = IconPlacement.Targets([target.IsCollapsed ? target : target.ToggleCollapsed()], Monitors, IconGridMetrics.Default);
        var plan = Plan(target, snapshot, final, start.IsCollapsed);
        var buffer = new DesktopIconTarget[count]; var previousVisible = collapsing ? count : 0;
        for (var frame = 0; frame <= 100; frame++)
        {
            var raw = frame / 100.0; var progress = FenceTransitionAnimator.EasedProgress(raw, !collapsing);
            var content = new DesktopBounds(target.X + 1, target.Y + 54, target.Width - 2,
                (target.Height - 55) * (collapsing ? 1 - progress : progress));
            plan.Fill(progress, frame == 100, buffer, content);
            var visible = 0;
            for (var i = 0; i < count; i++)
            {
                var t = buffer[i];
                if (t.Position == parked[i].Position) continue;
                visible++;
                var icon = snapshot[i];
                var x = t.Position.X + icon.Bounds.X - icon.Position.X;
                var y = t.Position.Y + icon.Bounds.Y - icon.Position.Y;
                Assert.InRange(x, content.X, content.X + content.Width - icon.Bounds.Width);
                Assert.InRange(y, content.Y, content.Y + content.Height - icon.Bounds.Height);
            }
            Assert.True(collapsing ? visible <= previousVisible : visible >= previousVisible);
            previousVisible = visible;
        }
        Assert.Equal(collapsing ? 0 : count, previousVisible);
        Assert.Equal(final, buffer);
        // A final Shell completion cannot expose a local/header position: the whole group is already parked.
        if (collapsing) { plan.Fill(1, true, buffer, new(target.X, target.Y + 54, target.Width, 0)); Assert.Equal(parked, buffer); }
    }
    [Fact]
    public void PartialRowStaysParkedUntilItFitsAndThenUsesSharedAbsoluteProgress()
    {
        var (start, snapshot) = Seed(12, true); var target = start.ToggleCollapsed();
        var final = IconPlacement.Targets([target], Monitors, IconGridMetrics.Default);
        var parked = IconPlacement.Targets([start], Monitors, IconGridMetrics.Default);
        var plan = Plan(target, snapshot, final, true); var buffer = new DesktopIconTarget[12];
        plan.Fill(0.25, false, buffer, new(target.X, target.Y + 54, target.Width, 79));
        Assert.Equal(parked, buffer);
        plan.Fill(0.5, false, buffer, new(target.X, target.Y + 54, target.Width, 100));
        for (var i = 0; i < 5; i++) Assert.Equal(new(final[i].Position.X, final[i].Position.Y - 6), buffer[i].Position);
        for (var i = 5; i < 12; i++) Assert.Equal(parked[i], buffer[i]);
        Assert.Throws<ArgumentException>(() => new FenceIconAnimationPlan(target, snapshot, final, Monitors, true, [], 54));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FenceIconAnimationPlan(target, snapshot, final, Monitors, true, parked, double.NaN));
    }
    [Theory]
    [InlineData(1)][InlineData(5)][InlineData(12)]
    public void ShellParkingCanFinishBeforeLastFenceFrameButExpandCannotFinishEarly(int count)
    {
        var (expanded, snapshot) = Seed(count, false); var collapsed = expanded.ToggleCollapsed();
        var parked = IconPlacement.Targets([collapsed], Monitors, IconGridMetrics.Default);
        var collapse = Plan(collapsed, snapshot, parked, false);
        var buffer = new DesktopIconTarget[count];
        collapse.Fill(0, false, buffer, new(expanded.X, expanded.Y + 54, expanded.Width, expanded.Height - 54));
        Assert.False(collapse.CanFinishParking(buffer));
        // The body still has height, but the last complete glyph no longer fits.
        collapse.Fill(0.9, false, buffer, new(expanded.X, expanded.Y + 54, expanded.Width, 30));
        Assert.True(collapse.CanFinishParking(buffer));
        collapse.Fill(1, false, buffer, new(expanded.X, expanded.Y + 54, expanded.Width, 0));
        Assert.True(collapse.CanFinishParking(buffer));
        Assert.False(collapse.CanFinishParking([]));
        var expand = Plan(expanded, snapshot, IconPlacement.Targets([expanded], Monitors, IconGridMetrics.Default), true);
        expand.Fill(0, false, buffer, new(expanded.X, expanded.Y + 54, expanded.Width, 0));
        Assert.Equal(parked, buffer);
        Assert.False(expand.CanFinishParking(buffer));
    }
}
