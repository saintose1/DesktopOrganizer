using System.Runtime.InteropServices;
using System.Text.Json;
using DesktopOrganizer.Core.Desktop;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.Infrastructure.Interop;

/// <summary>
/// Hidden-attribute layer for items of collapsed groups. Only items this application hid are ever un-hidden;
/// the journal is written before an attribute changes, so a crash cannot strand a user file as hidden.
/// </summary>
public sealed partial class DesktopIconService
{
    private readonly string? _concealmentPath;
    private readonly HashSet<string> _concealed = new(FenceIconLayout.IdentityComparer);
    private readonly HashSet<string> _concealmentDenied = new(FenceIconLayout.IdentityComparer);
    private readonly Lock _concealmentLock = new();

    public IReadOnlyCollection<string> Concealed { get { lock (_concealmentLock) return _concealed.ToArray(); } }

    public Task<IReadOnlyList<string>> ConcealAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default) =>
        _worker.Run<IReadOnlyList<string>>(() =>
        {
            if (_concealmentPath is null || ShowsHiddenItems()) return [];
            var candidates = ids.Distinct(FenceIconLayout.IdentityComparer)
                .Where(id => { lock (_concealmentLock) return !_concealed.Contains(id); })
                .Where(id => !_concealmentDenied.Contains(id) && CanConceal(id)).ToArray();
            if (candidates.Length == 0) return [];
            SaveConcealment(candidates); // journal first, even if the process stops mid-batch
            var hidden = new List<string>(candidates.Length);
            var denied = 0;
            foreach (var id in candidates)
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    var attributes = File.GetAttributes(id);
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    File.SetAttributes(id, attributes | FileAttributes.Hidden);
                    lock (_concealmentLock) _concealed.Add(id);
                    hidden.Add(id);
                }
                catch (UnauthorizedAccessException) { if (_concealmentDenied.Add(id)) denied++; }
                catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException)
                { /* Unwritable items stay parked. */ }
            }
            // Until Explorer drops them, a native drop can still reflow these items. One SHCNE_UPDATEDIR per folder
            // removes a whole group in about 0.55 s (15 items), versus ~1.1-1.5 s for per-item SHCNE_DELETE.
            foreach (var folder in hidden.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                NotifyItem(0x1000, folder);
            if (hidden.Count != candidates.Length) SaveConcealment();
            cancellationToken.ThrowIfCancellationRequested();
            if (denied != 0) _logger.LogInformation("Windows denied changes to {Count} collapsed items; they remain parked", denied);
            if (hidden.Count != 0) _logger.LogInformation("Concealed {Count} collapsed-group items with the Hidden attribute", hidden.Count);
            return hidden;
        }, cancellationToken);

    // Never cancelled: an item this application hid must always be returned.
    public Task RevealAsync(IReadOnlyList<(string Id, DesktopPoint? Position)> items, CancellationToken cancellationToken = default) =>
        _worker.Run(() =>
        {
            var mine = items.Where(i => { lock (_concealmentLock) return _concealed.Contains(i.Id); })
                .DistinctBy(i => i.Id, FenceIconLayout.IdentityComparer).ToArray();
            if (mine.Length == 0) return true;
            var ready = new List<(string Id, DesktopPoint? Position)>(mine.Length);
            var journalChanged = false;
            foreach (var item in mine)
            {
                try
                {
                    var attributes = File.GetAttributes(item.Id);
                    if ((attributes & FileAttributes.Hidden) != 0) File.SetAttributes(item.Id, attributes & ~FileAttributes.Hidden);
                    ready.Add(item);
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { _logger.LogWarning(error, "Could not clear the Hidden attribute of a collapsed-group item; it stays journaled"); continue; }
                lock (_concealmentLock) journalChanged |= _concealed.Remove(item.Id);
            }
            if (journalChanged) SaveConcealment();
            // Waiting for deleted or still-hidden files would hold Explorer redraw until the timeout.
            if (ready.Count == 0) return true;
            try
            {
                if (_session is null || !_discovery.Validate(_session.Handles)) _ = GetSession();
                var shown = _session!.Reveal(ready, CancellationToken.None);
                if (shown.Count != ready.Count) _logger.LogWarning("Explorer showed {Shown} of {Count} revealed items within the timeout", shown.Count, ready.Count);
            }
            catch (Exception error)
            {
                // Attributes are already cleared; Explorer's own watcher shows the items shortly.
                foreach (var item in ready) NotifyItem(0x2, item.Id);
                _logger.LogWarning(error, "Revealed items without placement; Explorer will show them on its own");
            }
            return true;
        });

    private static bool CanConceal(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !Path.IsPathFullyQualified(id)) return false; // virtual items stay parked only
        try
        {
            var attributes = File.GetAttributes(id);
            return (attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }
    private static bool ShowsHiddenItems()
    {
        var state = new ShellState();
        SHGetSetSettings(ref state, 0x1, false); // SSF_SHOWALLOBJECTS
        return (state.Flags & 1) != 0;
    }
    // PATHW | FLUSHNOWAIT: a synchronous FLUSH costs ~40 ms per item in Explorer and stalls the expand;
    // callers confirm the result by reading the view instead.
    private static void NotifyItem(int change, string path) => SHChangeNotify(change, 0x2005, path, null);

    private void LoadConcealment()
    {
        if (_concealmentPath is null || !File.Exists(_concealmentPath)) return;
        try
        {
            var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_concealmentPath)) ?? [];
            lock (_concealmentLock) foreach (var id in ids.Where(Path.IsPathFullyQualified)) _concealed.Add(id);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            // Keep the unreadable journal for manual recovery; never overwrite it.
            var copy = _concealmentPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            try { File.Copy(_concealmentPath, copy); } catch (IOException) { }
            _logger.LogError(error, "Cannot read the hidden-items journal; a copy was kept at {Path}", copy);
        }
    }
    private void SaveConcealment(string[]? pending = null)
    {
        if (_concealmentPath is null) return;
        string[] ids;
        lock (_concealmentLock) ids = _concealed.Concat(pending ?? []).ToArray();
        var temporary = _concealmentPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_concealmentPath)!);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, ids); file.Flush(flushToDisk: true); }
            File.Move(temporary, _concealmentPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct ShellState { public uint Flags; public uint Rest0, Rest1, Rest2, Rest3, Rest4, Rest5, Rest6, Rest7; }
    [DllImport("shell32.dll")] private static extern void SHGetSetSettings(ref ShellState state, uint mask, [MarshalAs(UnmanagedType.Bool)] bool set);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void SHChangeNotify(int change, uint flags, string first, string? second);
}
