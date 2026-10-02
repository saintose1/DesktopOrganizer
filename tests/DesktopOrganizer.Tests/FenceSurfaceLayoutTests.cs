using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Tests;

public sealed class FenceSurfaceLayoutTests
{
    private static Fence Fence(double x, double y, double width, double height) => new(Guid.NewGuid(), "Test", x, y, width, height);
    [Fact]
    public void PreservesVisibleFenceCoordinatesAndDimensions()
        => Assert.Equal(new FenceSurfaceLayout(120, 80, 450, 350), FenceSurfaceLayout.Clip(Fence(120, 80, 450, 350), new(0, 0, 1920, 1080)));
    [Fact]
    public void ClipsPartlyOffscreenFence()
        => Assert.Equal(new FenceSurfaceLayout(0, 0, 200, 100), FenceSurfaceLayout.Clip(Fence(-100, -100, 300, 200), new(0, 0, 1920, 1080)));
    [Fact]
    public void SupportsNegativeVirtualScreenOrigin()
        => Assert.Equal(new FenceSurfaceLayout(-1800, 100, 400, 300), FenceSurfaceLayout.Clip(Fence(-1800, 100, 400, 300), new(-1920, 0, 3840, 1080)));
    [Fact]
    public void OffscreenFenceProducesNoSurface()
        => Assert.Null(FenceSurfaceLayout.Clip(Fence(2000, 0, 300, 200), new(0, 0, 1920, 1080)));
    [Fact]
    public void HugeModelSizeIsClippedBeforePixelAllocation()
        => Assert.Equal(new FenceSurfaceLayout(0, 0, 1920, 1080), FenceSurfaceLayout.Clip(Fence(0, 0, double.MaxValue, double.MaxValue), new(0, 0, 1920, 1080)));
    [Fact]
    public void FractionalBoundsCoverVisiblePixels()
        => Assert.Equal(new FenceSurfaceLayout(120, 80, 451, 351), FenceSurfaceLayout.Clip(Fence(120.5, 80.5, 450, 350), new(0, 0, 1920, 1080)));
    [Fact]
    public void EmptyDesktopAreaProducesNoSurface()
        => Assert.Null(FenceSurfaceLayout.Clip(Fence(0, 0, 300, 200), new(0, 0, 0, 0)));
}
