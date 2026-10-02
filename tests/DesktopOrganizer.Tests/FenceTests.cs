using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Tests;

public sealed class FenceTests
{
    [Fact]
    public void ValidFencePreservesIdAndGeometryAndTrimsName()
    {
        var id = Guid.NewGuid();
        var fence = new Fence(id, "  Работа  ", -20.5, 10, 320, 240);
        Assert.Equal(id, fence.Id);
        Assert.Equal("Работа", fence.Name);
        Assert.Equal(-20.5, fence.X);
        Assert.Equal(10, fence.Y);
        Assert.Equal(320, fence.Width);
        Assert.Equal(240, fence.Height);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    public void RejectsInvalidName(string? name)
        => Assert.Throws<ArgumentException>(() => new Fence(Guid.NewGuid(), name!, 0, 0, 1, 1));

    [Fact]
    public void RejectsLongName()
        => Assert.Throws<ArgumentException>(() => new Fence(Guid.NewGuid(), new string('a', 101), 0, 0, 1, 1));

    [Fact]
    public void AcceptsMaximumNameLength()
        => Assert.Equal(100, new Fence(Guid.NewGuid(), new string('a', 100), 0, 0, 1, 1).Name.Length);

    [Fact]
    public void RejectsEmptyId()
        => Assert.Throws<ArgumentException>(() => new Fence(Guid.Empty, "Работа", 0, 0, 1, 1));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsInvalidDimensions(double value)
    {
        Assert.Throws<ArgumentException>(() => new Fence(Guid.NewGuid(), "Работа", 0, 0, value, 1));
        Assert.Throws<ArgumentException>(() => new Fence(Guid.NewGuid(), "Работа", 0, 0, 1, value));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsNonFiniteCoordinates(double value)
    {
        Assert.Throws<ArgumentException>(() => new Fence(Guid.NewGuid(), "Работа", value, 0, 1, 1));
        Assert.Throws<ArgumentException>(() => new Fence(Guid.NewGuid(), "Работа", 0, value, 1, 1));
    }
}
