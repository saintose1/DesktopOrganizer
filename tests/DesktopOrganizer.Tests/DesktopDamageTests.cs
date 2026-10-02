using DesktopOrganizer.Core.Desktop;

namespace DesktopOrganizer.Tests;

public sealed class DesktopDamageTests
{
    [Fact]
    public void OldAndNewFootprintsRemainSeparateRatherThanDirtyingTheGap()
    {
        var damage = DesktopDamage.Clip([new(10, 20, 100, 80), new(900, 700, 100, 80)], new(0, 0, 1920, 1080));
        Assert.Equal([new DesktopArea(10, 20, 100, 80), new(900, 700, 100, 80)], damage);
    }
    [Fact]
    public void OffscreenParkingCannotInvalidateTheEntireDesktop()
    {
        Assert.Equal(new DesktopArea(100, 200, 120, 100), Assert.Single(
            DesktopDamage.Clip([new(-8192, -8192, 120, 100), new(100, 200, 120, 100)], new(0, 0, 2560, 1600))));
    }
    [Fact]
    public void NegativeMonitorOriginIsTranslatedIntoListViewClientCoordinates()
    {
        Assert.Equal(new DesktopArea(0, 0, 120, 110), Assert.Single(
            DesktopDamage.Clip([new(-1940, -310, 140, 120)], new(-1920, -300, 4480, 1900))));
    }
    [Fact]
    public void FractionalDpiBoundsRoundOutwardsAndClipAtTheViewportEdge()
    {
        Assert.Equal(new DesktopArea(99, 199, 21, 21), Assert.Single(
            DesktopDamage.Clip([new(99.5, 199.5, 50.2, 40.2)], new(0, 0, 120, 220))));
    }
    [Fact]
    public void EmptyAndFullyOccludedDamageDoesNotRequestRepaint()
    {
        Assert.Empty(DesktopDamage.Clip([new(10, 10, 0, 0), new(2000, 2000, 100, 100)], new(0, 0, 1920, 1080)));
    }
}
