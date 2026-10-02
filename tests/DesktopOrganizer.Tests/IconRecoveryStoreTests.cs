using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopOrganizer.Tests;

public sealed class IconRecoveryStoreTests
{
    [Fact]
    public void JournalRoundTripAndAtomicReplacement()
    {
        using var directory = new TestDirectory();
        var store = new JsonIconRecoveryStore(directory.DirectoryPath, NullLogger<JsonIconRecoveryStore>.Instance);
        Assert.Empty(store.Load());
        DesktopIconTarget[] targets = [new("A", new(-500, 100)), new("B", new(200, 300))];
        store.Save(targets); Assert.Equal(targets, store.Load()); store.Save([]); Assert.Empty(store.Load());
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath, "*.tmp"));
    }
    [Fact]
    public void CorruptJournalIsPreservedAndPreventsNewParking()
    {
        using var directory = new TestDirectory(); var path = System.IO.Path.Combine(directory.DirectoryPath, "parked-icons.json");
        File.WriteAllText(path, "{ broken");
        Assert.Throws<System.Text.Json.JsonException>(() => new JsonIconRecoveryStore(directory.DirectoryPath, NullLogger<JsonIconRecoveryStore>.Instance).Load());
        Assert.Equal("{ broken", File.ReadAllText(path));
    }
    [Fact]
    public void InvalidReturnCoordinatesCannotOverwriteExistingJournal()
    {
        using var directory = new TestDirectory(); var store = new JsonIconRecoveryStore(directory.DirectoryPath, NullLogger<JsonIconRecoveryStore>.Instance);
        store.Save([new("A", new(100, 200))]);
        Assert.Throws<InvalidDataException>(() => store.Save([new("A", new(double.NaN, 200))]));
        Assert.Equal(new DesktopPoint(100, 200), store.Load()[0].Position);
    }
    [Fact]
    public void DuplicateIdentitiesAreRejected()
    {
        using var directory = new TestDirectory();
        Assert.Throws<InvalidDataException>(() => new JsonIconRecoveryStore(directory.DirectoryPath, NullLogger<JsonIconRecoveryStore>.Instance)
            .Save([new("A", new(0, 0)), new("a", new(1, 1))]));
    }
}
