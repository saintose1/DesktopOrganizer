using System.Diagnostics;

namespace DesktopOrganizer.Infrastructure;

public sealed record ProgramShortcut(string Name, string Path);

public sealed class ProgramShortcutSearch
{
    public IReadOnlyList<ProgramShortcut> Load()
    {
        var programs = new Dictionary<string, ProgramShortcut>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, recursive) in new[]
        {
            (Environment.SpecialFolder.DesktopDirectory, false), (Environment.SpecialFolder.CommonDesktopDirectory, false),
            (Environment.SpecialFolder.Programs, true), (Environment.SpecialFolder.CommonPrograms, true)
        })
        {
            var directory = Environment.GetFolderPath(folder);
            if (!Directory.Exists(directory)) continue;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
            };
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*.lnk", options))
                    if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                        programs.TryAdd(path, new(Path.GetFileNameWithoutExtension(path), path));
            }
            catch (IOException) { /* A directory can be removed while it is being enumerated. */ }
            catch (UnauthorizedAccessException) { /* Continue with accessible folders. */ }
        }
        return programs.Values.ToArray();
    }

    public static IReadOnlyList<ProgramShortcut> Match(IEnumerable<ProgramShortcut> shortcuts, string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return shortcuts.Where(item => words.All(word => item.Name.Contains(word, StringComparison.CurrentCultureIgnoreCase)))
            .OrderByDescending(item => item.Name.StartsWith(query.Trim(), StringComparison.CurrentCultureIgnoreCase))
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).Take(16).ToArray();
    }

    public void Open(ProgramShortcut shortcut)
    {
        if (!File.Exists(shortcut.Path)) throw new FileNotFoundException();
        using var launched = Process.Start(new ProcessStartInfo { FileName = shortcut.Path, UseShellExecute = true });
    }
}
