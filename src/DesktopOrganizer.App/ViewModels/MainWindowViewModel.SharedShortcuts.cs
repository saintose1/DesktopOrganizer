using System.IO;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Localization;
using DesktopOrganizer.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public IReadOnlyList<string> SharedShortcutPaths => _sharedShortcuts is { } migration
        ? _fences.SelectMany(fence => fence.Icons).Select(icon => icon.StableId)
            .Where(migration.IsSharedShortcut).ToArray()
        : [];

    public bool HasSharedShortcuts => CreateCommand.CanExecute(null) && SharedShortcutPaths.Count != 0;

    public void MoveSharedShortcuts(IReadOnlyList<string> confirmedPaths)
    {
        if (_sharedShortcuts is null || !CreateCommand.CanExecute(null) || !FlushAppearanceChanges()) return;
        var stillAssigned = SharedShortcutPaths.ToHashSet(FenceIconLayout.IdentityComparer);
        var original = _fences.ToArray();
        var saved = original;
        var moved = 0;
        var failures = new List<string>();
        foreach (var source in confirmedPaths.Where(stillAssigned.Contains).Distinct(FenceIconLayout.IdentityComparer))
        {
            var currentPath = source;
            try
            {
                if (_sharedShortcuts.Move(source, destination =>
                    {
                        var groups = saved.Select(fence =>
                        {
                            if (!fence.Icons.Any(icon => FenceIconLayout.IdentityComparer.Equals(icon.StableId, currentPath))) return fence;
                            return fence.WithIcons(fence.Icons.Select(icon => FenceIconLayout.IdentityComparer.Equals(icon.StableId, currentPath)
                                ? new DesktopIconAssignment(destination, icon.DisplayName, icon.OffsetX, icon.OffsetY) : icon));
                        }).ToArray();
                        if (!TrySave(groups)) return false;
                        saved = groups;
                        currentPath = destination;
                        return true;
                    })) moved++;
                else failures.Add(Path.GetFileName(source));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                failures.Add(Path.GetFileName(source));
                _logger.LogWarning("Shared shortcut migration could not finish ({ErrorType})", error.GetType().Name);
            }
        }
        if (saved != original) ApplySavedFences(saved);
        Status = Texts.F("Перенесено общих ярлыков: {0}.", moved);
        if (failures.Count != 0)
            Error = Texts.F("Не удалось завершить перенос: {0}. Проверьте права и наличие одноимённых ярлыков.", string.Join(", ", failures));
    }
}
