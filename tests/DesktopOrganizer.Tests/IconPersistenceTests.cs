using System.Text.Json;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopOrganizer.Tests;

public sealed class IconPersistenceTests
{
    private static JsonFenceStore Store(string path) => new(path, NullLogger<JsonFenceStore>.Instance);
    private static Fence Group(string id = "A") => new(Guid.NewGuid(), "Work", -120, 80, 450, 350, true,
        [new DesktopIconAssignment(id, "Shortcut", 45, 66)]);

    [Fact]
    public void AssignmentsAndCollapsedBoundsSurviveRestart()
    {
        using var directory = new TestDirectory();
        var original = Group();
        Store(directory.DirectoryPath).Save([original]);
        var restored = Assert.Single(Store(directory.DirectoryPath).Load().Fences);
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.X, restored.X);
        Assert.Equal(original.Width, restored.Width);
        Assert.Equal(original.Height, restored.Height);
        Assert.True(restored.IsCollapsed);
        Assert.Equal(original.Icons.ToArray(), restored.Icons.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JsonFromBothPreviousMilestonesMigratesWithoutAssignments(bool collapsed)
    {
        using var directory = new TestDirectory();
        var store = Store(directory.DirectoryPath);
        File.WriteAllText(store.FilePath, $$"""[{"Id":"{{Guid.NewGuid()}}","Name":"Legacy","X":120,"Y":80,"Width":450,"Height":350,"IsCollapsed":{{collapsed.ToString().ToLowerInvariant()}}}]""");
        var legacy = Assert.Single(store.Load().Fences);
        Assert.Empty(legacy.Icons);
        Assert.Equal(collapsed, legacy.IsCollapsed);
        store.Save([legacy.WithIcons(Group().Icons)]);
        Assert.Single(Assert.Single(Store(directory.DirectoryPath).Load().Fences).Icons);
    }

    [Fact]
    public void DuplicateCrossFenceOwnershipCannotReplaceValidJson()
    {
        using var directory = new TestDirectory();
        var store = Store(directory.DirectoryPath);
        store.Save([Group()]);
        var before = File.ReadAllText(store.FilePath);
        Assert.Throws<ArgumentException>(() => store.Save([Group("A"), Group("a")]));
        Assert.Equal(before, File.ReadAllText(store.FilePath));
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"StableId\":\"\",\"DisplayName\":\"A\",\"OffsetX\":0,\"OffsetY\":0}]")]
    [InlineData("[{\"StableId\":\"A\",\"DisplayName\":\"A\"}]")]
    public void CorruptAssignmentsAreBackedUpWithoutOverwritingOriginal(string assignments)
    {
        using var directory = new TestDirectory();
        var store = Store(directory.DirectoryPath);
        var json = $$"""[{"Id":"{{Guid.NewGuid()}}","Name":"Work","X":0,"Y":0,"Width":450,"Height":350,"Icons":{{assignments}}}]""";
        File.WriteAllText(store.FilePath, json);
        Assert.NotNull(store.Load().Warning);
        var backup = Assert.Single(Directory.GetFiles(directory.DirectoryPath, "fences.json.corrupt-*"));
        Assert.Equal(json, File.ReadAllText(backup));
    }

    [Fact]
    public void DuplicateOwnershipOnDiskIsRejectedAndPreserved()
    {
        using var directory = new TestDirectory();
        var store = Store(directory.DirectoryPath);
        var json = JsonSerializer.Serialize(new[] { Group(), Group() });
        File.WriteAllText(store.FilePath, json);
        Assert.NotNull(store.Load().Warning);
        Assert.Equal(json, File.ReadAllText(Assert.Single(Directory.GetFiles(directory.DirectoryPath, "fences.json.corrupt-*"))));
    }
}
