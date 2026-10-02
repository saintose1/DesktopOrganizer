using DesktopOrganizer.Core.Desktop;

namespace DesktopOrganizer.Core.Storage;

public interface IIconRecoveryStore
{
    IReadOnlyList<DesktopIconTarget> Load();
    void Save(IReadOnlyList<DesktopIconTarget> targets);
}
