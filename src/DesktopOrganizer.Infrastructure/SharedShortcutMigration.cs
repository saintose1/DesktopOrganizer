using System.Security.Cryptography;
using DesktopOrganizer.Infrastructure.Interop;

namespace DesktopOrganizer.Infrastructure;

/// <summary>Converts an assigned shared .lnk into a personal shortcut while keeping the original in the profile backup.</summary>
public sealed class SharedShortcutMigration
{
    private readonly string _shared;
    private readonly string _personal;
    private readonly string _backups;

    public SharedShortcutMigration(string dataDirectory) : this(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), dataDirectory) { }

    internal SharedShortcutMigration(string shared, string personal, string dataDirectory)
    {
        _shared = shared;
        _personal = personal;
        _backups = Path.Combine(dataDirectory, "shared-shortcut-backups");
    }

    public bool IsSharedShortcut(string path) => !string.IsNullOrEmpty(_shared) &&
        Path.IsPathFullyQualified(path) && Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
        StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(path), _shared);

    public bool Move(string source, Func<string, bool> saveAssignment)
    {
        if (!IsSharedShortcut(source) || !Directory.Exists(_personal) ||
            StringComparer.OrdinalIgnoreCase.Equals(_shared, _personal) || !File.Exists(source))
            throw new IOException("A shared desktop shortcut or the personal desktop is unavailable.");
        if ((File.GetAttributes(source) & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System)) != 0)
            throw new IOException("The shared shortcut has unsupported attributes.");
        if (!NativeMethods.CanOpenFileFor(source, 0x10000)) throw new UnauthorizedAccessException(); // DELETE

        var destination = Path.Combine(_personal, Path.GetFileName(source));
        var archiveDirectory = Path.Combine(_backups, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archiveDirectory);
        var archive = Path.Combine(archiveDirectory, Path.GetFileName(source));
        File.Copy(source, destination);
        var saved = false;
        try
        {
            if (!NativeMethods.CanOpenFileFor(destination, 0x100) || !SameContents(source, destination)) // FILE_WRITE_ATTRIBUTES
                throw new IOException("Could not verify the personal shortcut.");
            saved = saveAssignment(destination);
            if (!saved) return false;

            try { File.Move(source, archive); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A failed source move must leave a valid assigned shortcut even if saving the old assignment fails.
                if (File.Exists(source)) saved = !saveAssignment(source);
                throw;
            }
            return true;
        }
        finally
        {
            if (!saved && File.Exists(source) && File.Exists(destination) && SameContents(source, destination))
                File.Delete(destination);
        }
    }

    private static bool SameContents(string first, string second)
    {
        using var source = File.OpenRead(first);
        using var copy = File.OpenRead(second);
        return SHA256.HashData(source).AsSpan().SequenceEqual(SHA256.HashData(copy));
    }
}
