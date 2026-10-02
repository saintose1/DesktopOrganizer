using DesktopOrganizer.Infrastructure.Interop;

namespace DesktopOrganizer.Tests;

public sealed class BackgroundMouseRouteTests
{
    [Fact]
    public void BackgroundPressIsHandedOffUntilExplorerCaptures()
    {
        var route = new BackgroundMouseRoute();
        Assert.Equal(0x201u, route.Observe(1, true, false, true));
        Assert.Equal(0x200u, route.Observe(0, true, false, true));
        Assert.Equal(0u, route.Observe(0, true, true, true));
        Assert.Equal(0u, route.Observe(0, true, false, true));
        Assert.Equal(0u, route.Observe(2, true, false, false));
    }

    [Fact]
    public void FastClickQueuesReleaseEvenBeforeNativeCaptureStarts()
    {
        var route = new BackgroundMouseRoute();
        Assert.Equal(0x201u, route.Observe(1, true, false, false));
        Assert.Equal(0x202u, route.Observe(2, false, false, false));
        Assert.Equal(0u, route.Observe(2, true, false, false));
    }

    [Theory]
    [InlineData(false, false)] // Actual icon, Fence header/border, or another app.
    [InlineData(true, true)] // An existing Explorer/Fence/app capture.
    [InlineData(false, true)]
    public void ExistingNativeInputIsNeverDuplicated(bool background, bool captured)
    {
        var route = new BackgroundMouseRoute();
        Assert.Equal(0u, route.Observe(1, background, captured, true));
        Assert.Equal(0u, route.Observe(0, true, false, true));
        Assert.Equal(0u, route.Observe(2, true, false, false));
    }

    [Fact]
    public void LostReleaseOrDisconnectDoesNotLeakIntoTheNextGesture()
    {
        var route = new BackgroundMouseRoute();
        route.Observe(1, true, false, true);
        Assert.Equal(0u, route.Observe(0, true, false, false));
        route.Observe(1, true, false, true);
        route.Reset();
        Assert.Equal(0u, route.Observe(0, true, false, true));
    }

    [Fact]
    public void ANewPressOnInteractiveContentCancelsPendingHandoff()
    {
        var route = new BackgroundMouseRoute();
        route.Observe(1, true, false, true);
        Assert.Equal(0u, route.Observe(1, false, false, true));
        Assert.Equal(0u, route.Observe(0, true, false, true));
    }

    [Theory]
    [InlineData(-2000, -300)]
    [InlineData(600, 1600)]
    [InlineData(-32768, 32767)]
    public void MouseMessageCoordinatesPreserveSignedMonitorPositions(int x, int y)
    {
        Assert.True(MouseMessagePoint.TryPack(x, y, out var parameter));
        var result = MouseMessagePoint.Unpack(unchecked((uint)(long)parameter));
        Assert.Equal(x, result.X); Assert.Equal(y, result.Y);
    }

    [Theory]
    [InlineData(32768, 0)]
    [InlineData(0, -32769)]
    public void UnrepresentableCoordinatesAreRejectedRatherThanWrapped(int x, int y)
        => Assert.False(MouseMessagePoint.TryPack(x, y, out _));
}
