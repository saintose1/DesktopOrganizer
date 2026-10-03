using System.Text.Json;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Infrastructure.Interop;
using DesktopOrganizer.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopOrganizer.Tests;

public sealed class FenceAppearanceTests
{
    [Fact]
    public void OldJsonLoadsOriginalVisualDefaults()
    {
        using var directory = new TestDirectory();
        var store = new JsonFenceStore(directory.DirectoryPath, NullLogger<JsonFenceStore>.Instance);
        File.WriteAllText(store.FilePath, $$"""[{"Id":"{{Guid.NewGuid()}}","Name":"Legacy","X":5,"Y":10,"Width":300,"Height":200}]""");
        var result = store.Load();
        Assert.Null(result.Warning);
        Assert.Equal(FenceAppearance.Default, Assert.Single(result.Fences).Appearance);
        Assert.Equal(0x690A101Bu, AppearancePixels.FromHex(FenceAppearance.Default.BodyColor, FenceAppearance.Default.BodyOpacity));
    }

    [Fact]
    public void EveryPropertyRoundTripsAndSurvivesGeometryCollapseAndAssignments()
    {
        using var directory = new TestDirectory();
        var appearance = new FenceAppearance("Consolas", 22, true, true, FenceTitleAlignment.Right,
            "#dDcCfF", "#543210", 57, "#332244", 25, "#ABCD12", 65);
        var fence = new Fence(Guid.NewGuid(), "Styled", -50, 30, 500, 400, true,
            [new DesktopIconAssignment("stable", "Icon", 25, 70)], appearance);
        var store = new JsonFenceStore(directory.DirectoryPath, NullLogger<JsonFenceStore>.Instance);
        store.Save([fence]);
        var restored = Assert.Single(store.Load().Fences);
        Assert.Equal(appearance, restored.Appearance);
        Assert.Equal("#DDCCFF", restored.Appearance.TextColor);
        Assert.Equal(appearance, restored.WithBounds(20, 40, 550, 420).ToggleCollapsed().WithIcons([]).Appearance);
        Assert.Equal(fence.Id, restored.Id);
        Assert.Equal(fence.Icons.ToArray(), restored.Icons.ToArray());
    }

    [Theory]
    [InlineData(0)] [InlineData(100)]
    public void OpacityEndpointsAreValid(double value)
    {
        var a = new FenceAppearance(headerOpacity: value, bodyOpacity: value, borderOpacity: value);
        Assert.Equal(value, a.BodyOpacity);
        Assert.Equal(value == 0 ? 0u : 0xFFFFFFFFu, AppearancePixels.FromHex("#FFFFFF", value));
    }

    [Theory]
    [InlineData(-1)] [InlineData(101)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidOpacityIsRejected(double value)
    {
        Assert.Throws<ArgumentException>(() => new FenceAppearance(headerOpacity: value));
        Assert.Throws<ArgumentException>(() => new FenceAppearance(bodyOpacity: value));
        Assert.Throws<ArgumentException>(() => new FenceAppearance(borderOpacity: value));
    }

    [Theory]
    [InlineData(7)] [InlineData(29)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidFontSizeIsRejected(double value) => Assert.Throws<ArgumentException>(() => new FenceAppearance(fontSize: value));

    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData("bad\nfont")]
    public void InvalidFontNameIsRejected(string value) => Assert.Throws<ArgumentException>(() => new FenceAppearance(fontFamily: value));

    [Theory]
    [InlineData("red")] [InlineData("#123")] [InlineData("#FFFFFF00")] [InlineData("#XX1234")] [InlineData("# 12345")]
    public void InvalidColorsAreRejected(string value) => Assert.Throws<ArgumentException>(() => new FenceAppearance(bodyColor: value));

    [Fact]
    public void InvalidAlignmentIsRejected() => Assert.Throws<ArgumentException>(() => new FenceAppearance(alignment: (FenceTitleAlignment)44));

    [Fact]
    public void AlphaIsPremultipliedAndNeverLeavesRgbAtZeroOpacity()
    {
        var pixel = AppearancePixels.FromHex("#804020", 50);
        Assert.Equal(0x80402010u, pixel);
        Assert.Equal(0u, AppearancePixels.FromHex("#AAFFEE", 0));
    }

    [Fact]
    public void InvalidPersistedAppearancePreservesCorruptFile()
    {
        using var directory = new TestDirectory();
        var store = new JsonFenceStore(directory.DirectoryPath, NullLogger<JsonFenceStore>.Instance);
        var content = JsonSerializer.Serialize(new[] { new { Id = Guid.NewGuid(), Name = "Bad", X = 0, Y = 0, Width = 300, Height = 200, Appearance = new { FontSize = 999 } } });
        File.WriteAllText(store.FilePath, content);
        Assert.NotNull(store.Load().Warning);
        var backup = Assert.Single(Directory.GetFiles(directory.DirectoryPath, "fences.json.corrupt-*"));
        Assert.Equal(content, File.ReadAllText(backup));
    }
}
