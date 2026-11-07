using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopOrganizer.Tests;

public sealed class FenceAutoSortTests
{
    private static DesktopIcon Icon(string path) => new(0, path, Path.GetFileNameWithoutExtension(path), new(0, 0), new(0, 0), new(0, 0, 100, 100));

    [Fact]
    public void ParseNormalizesExtensionsAndKeepsWildcards()
    {
        Assert.Equal(["*.png", "*.jpg", "Steam*", "*.lnk"], FenceAutoSort.Parse("png; .jpg, Steam*  *.lnk png"));
        Assert.Empty(FenceAutoSort.Parse("  "));
        Assert.Throws<ArgumentException>(() => FenceAutoSort.Parse(@"C:\*.png"));
    }

    [Theory]
    [InlineData(@"C:\Users\u\Desktop\cat.PNG", "*.png", true)]
    [InlineData(@"C:\Users\u\Desktop\Steam.lnk", "Steam*", true)]
    [InlineData(@"C:\Users\u\Desktop\notes.txt", "*.png", false)]
    [InlineData(@"C:\Users\u\Desktop\a1.txt", "a?.txt", true)]
    [InlineData(@"C:\Users\u\Desktop\Folder", "Fold*", true)]
    public void MatchesFileNamesCaseInsensitively(string identity, string pattern, bool expected) =>
        Assert.Equal(expected, FenceAutoSort.Matches(identity, pattern));

    [Fact]
    public void OnlyUnassignedNewcomersJoinTheFirstMatchingGroup()
    {
        var pictures = new Fence(Guid.NewGuid(), "Pictures", 0, 0, 400, 300, autoSort: ["*.png"]);
        var games = new Fence(Guid.NewGuid(), "Games", 500, 0, 400, 300,
            icons: [new DesktopIconAssignment(@"C:\D\old.png", "old", 20, 50)], autoSort: ["*.png", "*.lnk"]);
        var result = FenceAutoSort.AssignNew([pictures, games],
            [Icon(@"C:\D\new.png"), Icon(@"C:\D\old.png"), Icon(@"C:\D\game.lnk"), Icon(@"C:\D\doc.txt")], IconGridMetrics.Default, _ => 1);
        Assert.Equal([@"C:\D\new.png"], result[0].Icons.Select(i => i.StableId));
        Assert.Equal([@"C:\D\old.png", @"C:\D\game.lnk"], result[1].Icons.Select(i => i.StableId));
    }

    [Fact]
    public void RulesSurviveSaveAndLoadAndOldFilesHaveNone()
    {
        using var directory = new TestDirectory();
        var store = new JsonFenceStore(directory.DirectoryPath, NullLogger<JsonFenceStore>.Instance);
        var fence = new Fence(Guid.NewGuid(), "Pictures", 0, 0, 400, 300).WithAutoSort(["*.png", "*.jpg"]);
        store.Save([fence, new Fence(Guid.NewGuid(), "Plain", 500, 0, 400, 300)]);
        var loaded = new JsonFenceStore(directory.DirectoryPath, NullLogger<JsonFenceStore>.Instance).Load().Fences;
        Assert.Equal(["*.png", "*.jpg"], loaded[0].AutoSort);
        Assert.Empty(loaded[1].AutoSort);
        Assert.Equal(["*.png", "*.jpg"], loaded[0].WithPresentation("Renamed", loaded[0].Appearance).AutoSort);
    }
}
