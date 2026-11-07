using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace DesktopOrganizer.Infrastructure.Interop;

public sealed record HiddenItemsRecoveryResult(int Restored, int Missing, IReadOnlyList<string> Failed);

/// <summary>
/// Standalone recovery for the Hidden-attribute layer: un-hides every item listed in hidden-icons.json without
/// Explorer automation or a running application (crash, uninstall, manual repair). Only journaled items are touched.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HiddenItemsRecovery
{
    public const string JournalName = "hidden-icons.json";

    public static HiddenItemsRecoveryResult RestoreAll(string storageDirectory)
    {
        var journal = Path.Combine(Path.GetFullPath(storageDirectory), JournalName);
        if (!File.Exists(journal)) return new(0, 0, []);
        var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(journal)) ?? [];
        int restored = 0, missing = 0;
        var failed = new List<string>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids.Where(Path.IsPathFullyQualified))
        {
            try
            {
                var attributes = File.GetAttributes(id);
                if ((attributes & FileAttributes.Hidden) != 0) File.SetAttributes(id, attributes & ~FileAttributes.Hidden);
                restored++;
                if (Path.GetDirectoryName(id) is { } folder) folders.Add(folder);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { missing++; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { failed.Add(id); }
        }
        foreach (var folder in folders) SHChangeNotify(0x1000, 0x2005, folder, null); // SHCNE_UPDATEDIR | PATHW | FLUSHNOWAIT
        // Keep only what could not be restored, so a later attempt still knows about it.
        var temporary = journal + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(failed));
            File.Move(temporary, journal, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(restored, missing, failed);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void SHChangeNotify(int change, uint flags, string first, string? second);
}
