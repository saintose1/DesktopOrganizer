using System.Text.Json;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Core.Storage;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.Infrastructure.Storage;

public sealed class JsonFenceStore(string dataDirectory, ILogger<JsonFenceStore> logger) : IFenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string FilePath { get; } = Path.Combine(Path.GetFullPath(dataDirectory), "fences.json");

    public FenceLoadResult Load()
    {
        try
        {
            // Open directly: File.Exists would hide permission and I/O failures.
            using var stream = File.OpenRead(FilePath);
            var documents = JsonSerializer.Deserialize<List<FenceDocument>>(stream, JsonOptions)
                ?? throw new JsonException("Ожидался массив групп.");
            var fences = documents.Select(document =>
                (document ?? throw new JsonException("Группа не может быть null.")).ToFence()).ToArray();
            if (fences.Select(fence => fence.Id).Distinct().Count() != fences.Length)
                throw new JsonException("Идентификаторы групп должны быть уникальными.");
            var identities = fences.SelectMany(f => f.Icons).Select(i => i.StableId).ToArray();
            if (identities.Distinct(StringComparer.OrdinalIgnoreCase).Count() != identities.Length)
                throw new JsonException("Иконка не может принадлежать нескольким группам.");
            logger.LogInformation("Loaded {Count} groups", fences.Length);
            return new FenceLoadResult(fences);
        }
        catch (FileNotFoundException) { return new FenceLoadResult([]); }
        catch (DirectoryNotFoundException) { return new FenceLoadResult([]); }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            logger.LogError(exception, "Invalid group data in {FilePath}", FilePath);
            return RecoverCorruptFile();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Could not read groups from {FilePath}", FilePath);
            return new FenceLoadResult([], "Не удалось прочитать группы. Изменения заблокированы, чтобы сохранить исходный файл. Проверьте доступ и перезапустите приложение.", false);
        }
    }

    public void Save(IReadOnlyCollection<Fence> fences)
    {
        ArgumentNullException.ThrowIfNull(fences);
        if (fences.Any(fence => fence is null) || fences.Select(fence => fence.Id).Distinct().Count() != fences.Count)
            throw new ArgumentException("Группы должны иметь уникальные идентификаторы.", nameof(fences));
        var identities = fences.SelectMany(f => f.Icons).Select(i => i.StableId).ToArray();
        if (identities.Distinct(StringComparer.OrdinalIgnoreCase).Count() != identities.Length)
            throw new ArgumentException("Иконка не может принадлежать нескольким группам.", nameof(fences));
        var temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, fences.Select(FenceDocument.FromFence).ToArray(), JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            // Same volume: the old file is never truncated before the new one is ready.
            if (File.Exists(FilePath)) File.Replace(temporaryPath, FilePath, null);
            else File.Move(temporaryPath, FilePath);
            logger.LogInformation("Saved {Count} groups", fences.Count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogError(exception, "Could not save groups to {FilePath}", FilePath);
            throw new FenceStorageException("Не удалось сохранить изменения. Предыдущее состояние сохранено. Проверьте доступ к папке и свободное место, затем повторите действие.", exception);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not remove temporary group file {FilePath}", temporaryPath);
            }
        }
    }

    private FenceLoadResult RecoverCorruptFile()
    {
        var backupPath = FilePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Move(FilePath, backupPath);
            logger.LogWarning("Corrupt group file preserved at {BackupPath}", backupPath);
            return new FenceLoadResult([], $"Файл групп повреждён. Исходный файл сохранён: {backupPath}. Можно создавать новые группы.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Could not preserve corrupt group file");
            return new FenceLoadResult([], "Файл групп повреждён, и его резервную копию создать не удалось. Изменения заблокированы. Проверьте доступ и перезапустите приложение.", false);
        }
    }

    private sealed record FenceDocument
    {
        public required Guid Id { get; init; }
        public required string Name { get; init; }
        public required double X { get; init; }
        public required double Y { get; init; }
        public required double Width { get; init; }
        public required double Height { get; init; }
        public bool IsCollapsed { get; init; } // Optional for files written by earlier versions.
        public FenceAppearance? Appearance { get; init; } // Missing in legacy JSON: use original visuals.
        public IconDocument[] Icons { get; init; } = []; // Migration of both previous milestones.
        public string[]? AutoSort { get; init; } // Missing in files written before auto-sort rules.
        public Fence ToFence() => new(Id, Name, X, Y, Width, Height, IsCollapsed,
            (Icons ?? throw new JsonException("Icons cannot be null.")).Select(i =>
                (i ?? throw new JsonException("Icon assignment cannot be null.")).ToAssignment()), Appearance, AutoSort ?? []);
        public static FenceDocument FromFence(Fence fence) => new()
        {
            Id = fence.Id, Name = fence.Name, X = fence.X, Y = fence.Y, Width = fence.Width, Height = fence.Height,
            IsCollapsed = fence.IsCollapsed,
            Appearance = fence.Appearance,
            AutoSort = fence.AutoSort.IsEmpty ? null : fence.AutoSort.ToArray(),
            Icons = fence.Icons.Select(i => new IconDocument { StableId = i.StableId, DisplayName = i.DisplayName, OffsetX = i.OffsetX, OffsetY = i.OffsetY }).ToArray()
        };
    }
    private sealed record IconDocument
    {
        public required string StableId { get; init; }
        public required string DisplayName { get; init; }
        public required double OffsetX { get; init; }
        public required double OffsetY { get; init; }
        public DesktopIconAssignment ToAssignment() => new(StableId, DisplayName, OffsetX, OffsetY);
    }
}
