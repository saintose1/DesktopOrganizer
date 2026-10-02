using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Core.Storage;

public interface IFenceStore
{
    FenceLoadResult Load();
    void Save(IReadOnlyCollection<Fence> fences);
}

public sealed record FenceLoadResult(IReadOnlyList<Fence> Fences, string? Warning = null, bool CanWrite = true);

public sealed class FenceStorageException(string message, Exception innerException) : Exception(message, innerException);
