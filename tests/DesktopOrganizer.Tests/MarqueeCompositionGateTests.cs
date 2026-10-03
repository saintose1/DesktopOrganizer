using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Infrastructure.Interop;

namespace DesktopOrganizer.Tests;

public sealed class MarqueeCompositionGateTests
{
    [Fact]
    public void ClickAndSmallJitterDoNotSwitchComposition()
    {
        var gate = Begin();
        Assert.False(gate.Classify(true));
        Assert.False(gate.Move(new(103, 97)));
        gate.End();
        Assert.False(gate.Move(new(300, 400)));
    }
    [Fact]
    public void RealMarqueeRequestsOneTransitionUntilRelease()
    {
        var gate = Begin(); gate.Classify(true);
        Assert.True(gate.Move(new(105, 100)));
        Assert.False(gate.Move(new(200, 200)));
        Assert.False(gate.Classify(true));
        gate.End();
        Assert.False(gate.Move(new(300, 400)));
    }
    [Fact]
    public void MotionBeforeClassificationStartsOnlyAnActiveBackgroundDrag()
    {
        var gate = Begin(); Assert.False(gate.Move(new(200, 200)));
        Assert.True(gate.Classify(true));
        gate = Begin(); gate.Move(new(200, 200)); gate.End();
        Assert.False(gate.Classify(true));
    }
    [Fact]
    public void IconDragNeverChangesMarqueeComposition()
    {
        var gate = Begin(); gate.Classify(false);
        Assert.False(gate.Move(new(300, 400)));
    }
    [Fact]
    public void NextPressDoesNotInheritMotionOrClassification()
    {
        var gate = Begin(); gate.Classify(true); gate.Move(new(200, 200)); gate.End();
        gate.Begin(new(-800, 200), 6, 8);
        Assert.False(gate.Classify(true));
        Assert.False(gate.Move(new(-794, 208)));
        Assert.True(gate.Move(new(-800, 209)));
    }
    private static MarqueeCompositionGate Begin()
    {
        var gate = new MarqueeCompositionGate(); gate.Begin(new(100, 100), 4, 4); return gate;
    }
}
