using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Core.Storage;
using DesktopOrganizer.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopOrganizer.Tests;

public sealed class JsonFenceStoreTests
{
    [Fact]
    public void LegacyJsonWithoutCollapseLoadsExpandedAndRoundTripsCollapsedNormalHeight()
    {
        using var directory = new TestDirectory();
        var store = Store(directory.DirectoryPath);
        var id = Guid.NewGuid();
        File.WriteAllText(store.FilePath, $$"""[{"Id":"{{id}}","Name":"Legacy","X":120,"Y":80,"Width":450,"Height":350}]""");
        var legacy = Assert.Single(store.Load().Fences);
        Assert.False(legacy.IsCollapsed);
        store.Save([legacy.ToggleCollapsed()]);
        var restored = Assert.Single(Store(directory.DirectoryPath).Load().Fences);
        Assert.True(restored.IsCollapsed);
        Assert.Equal(350, restored.Height);
        Assert.Equal(legacy, restored.ToggleCollapsed());
    }

    private static JsonFenceStore Store(string directory) => new(directory, NullLogger<JsonFenceStore>.Instance);

    [Fact]
    public void MissingDirectoryAndFileReturnEmptyWritableState()
    {
        using var directory = new TestDirectory();
        var store = Store(Path.Combine(directory.DirectoryPath, "not-created"));
        var result = store.Load();
        Assert.Empty(result.Fences);
        Assert.Null(result.Warning);
        Assert.True(result.CanWrite);
    }

    [Fact]
    public void SaveThenReadPreservesAllFieldsAndReplacesPreviousFile()
    {
        using var directory = new TestDirectory();
        var store = Store(directory.DirectoryPath);
        var first = new Fence(Guid.NewGuid(), "Работа", -12.5, 55, 640, 480);
        var second = new Fence(Guid.NewGuid(), "Игры", 100, -200, 300.25, 400.5);
        store.Save([first, second]);
        Assert.Equal(new[] { first, second }, Store(directory.DirectoryPath).Load().Fences);
        var renamed = new Fence(first.Id, "Документы", 30, 40, 500, 600);
        store.Save([renamed]);
        Assert.Equal(renamed, Assert.Single(Store(directory.DirectoryPath).Load().Fences));
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath, "*.tmp"));
        store.Save([]);
        Assert.Empty(Store(directory.DirectoryPath).Load().Fences);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[] trailing")]
    [InlineData("[{\"Name\":\"Missing fields\"}]")]
    [InlineData("[{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"Name\":\"\",\"X\":0,\"Y\":0,\"Width\":300,\"Height\":200}]")]
    [InlineData("[{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"Name\":\"A\",\"X\":0,\"Y\":0,\"Width\":0,\"Height\":200}]")]
    public void CorruptDataIsPreservedAndNewGroupsCanBeSaved(string json)
    {
        using var directory = new TestDirectory();
        var logger = new RecordingLogger<JsonFenceStore>();
        var store = new JsonFenceStore(directory.DirectoryPath, logger);
        File.WriteAllText(store.FilePath, json);
        var result = store.Load();
        Assert.Empty(result.Fences);
        Assert.True(result.CanWrite);
        Assert.NotNull(result.Warning);
        Assert.False(File.Exists(store.FilePath));
        var backup = Assert.Single(Directory.GetFiles(directory.DirectoryPath, "fences.json.corrupt-*"));
        Assert.Equal(json, File.ReadAllText(backup));
        Assert.Contains(LogLevel.Error, logger.Levels);
        var fence = new Fence(Guid.NewGuid(), "После восстановления", 0, 0, 300, 200);
        store.Save([fence]);
        Assert.Equal(fence, Assert.Single(Store(directory.DirectoryPath).Load().Fences));
        Assert.Equal(json, File.ReadAllText(backup));
    }

    [Fact]
    public void DuplicateIdsOnDiskAreRejected()
    {
        using var directory = new TestDirectory();
        var store = Store(directory.DirectoryPath);
        var fence = new Fence(Guid.NewGuid(), "Работа", 0, 0, 300, 200);
        var json = System.Text.Json.JsonSerializer.Serialize(new[] { fence, fence });
        File.WriteAllText(store.FilePath, json);
        Assert.NotNull(store.Load().Warning);
    }

    [Fact]
    public void FailedWritePreservesOldFileAndLogsError()
    {
        using var directory = new TestDirectory();
        var logger = new RecordingLogger<JsonFenceStore>();
        var store = new JsonFenceStore(directory.DirectoryPath, logger);
        var fence = new Fence(Guid.NewGuid(), "Работа", 0, 0, 300, 200);
        store.Save([fence]);
        var previous = File.ReadAllText(store.FilePath);
        using (File.Open(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<FenceStorageException>(() => store.Save([]));
        Assert.Equal(previous, File.ReadAllText(store.FilePath));
        Assert.Contains(LogLevel.Error, logger.Levels);
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void UnreadableFileDisablesEditingWithoutOverwritingData()
    {
        using var directory = new TestDirectory();
        var store = Store(directory.DirectoryPath);
        store.Save([]);
        using var locked = File.Open(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = store.Load();
        Assert.False(result.CanWrite);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void CannotBackupCorruptionDisablesEditing()
    {
        using var directory = new TestDirectory();
        var store = Store(directory.DirectoryPath);
        File.WriteAllText(store.FilePath, "broken");
        using var locked = File.Open(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = store.Load();
        Assert.False(result.CanWrite);
        Assert.NotNull(result.Warning);
        Assert.True(File.Exists(store.FilePath));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }
}
