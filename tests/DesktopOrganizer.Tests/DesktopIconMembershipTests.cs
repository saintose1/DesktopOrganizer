using DesktopOrganizer.Infrastructure.Interop;

namespace DesktopOrganizer.Tests;

/// <summary>Filesystem absence checks only; no Explorer item or user file is changed by the service.</summary>
public sealed class DesktopIconMembershipTests
{
    [Fact]
    public void ExistingFileRemainsUntouchedAndIsNotMissing()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.DirectoryPath, "shortcut.lnk");
        File.WriteAllText(path, "test shortcut content");

        Assert.False(DesktopIconService.IsConfirmedMissingFile(path));
        Assert.Equal("test shortcut content", File.ReadAllText(path));
    }

    [Fact]
    public void DeletedFileIsConfirmedMissing()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.DirectoryPath, "shortcut.lnk");
        File.WriteAllText(path, "test shortcut content");
        File.Delete(path);

        Assert.True(DesktopIconService.IsConfirmedMissingFile(path));
    }

    [Fact]
    public void DeletedParentDirectoryConfirmsFileAbsence()
    {
        using var directory = new TestDirectory();
        var parent = Path.Combine(directory.DirectoryPath, "uninstalled");
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, "shortcut.lnk");
        File.WriteAllText(path, "test shortcut content");
        File.Delete(path);
        Directory.Delete(parent);

        Assert.True(DesktopIconService.IsConfirmedMissingFile(path));
    }

    [Fact]
    public void AnExistingDirectoryIsNotAnAbsentDesktopItem()
    {
        using var directory = new TestDirectory();
        Assert.False(DesktopIconService.IsConfirmedMissingFile(directory.DirectoryPath));
    }

    [Fact]
    public void RestoredFileStopsBeingConfirmedMissing()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.DirectoryPath, "shortcut.lnk");
        Assert.True(DesktopIconService.IsConfirmedMissingFile(path));
        File.WriteAllText(path, "restored shortcut content");

        Assert.False(DesktopIconService.IsConfirmedMissingFile(path));
    }

    [Theory]
    [InlineData("::{645FF040-5081-101B-9F08-00AA002F954E}")]
    [InlineData("shell:RecycleBinFolder")]
    [InlineData("shortcut.lnk")]
    [InlineData(@"relative\shortcut.lnk")]
    [InlineData(@"C:shortcut.lnk")]
    [InlineData("")]
    [InlineData(" ")]
    public void VirtualAndRelativeIdentitiesCannotBeInferredMissing(string identity)
    {
        Assert.False(DesktopIconService.IsConfirmedMissingFile(identity));
    }

    [Fact]
    public void InvalidAbsolutePathIsNotDeletionEvidence()
    {
        using var directory = new TestDirectory();
        Assert.False(DesktopIconService.IsConfirmedMissingFile(Path.Combine(directory.DirectoryPath, "invalid\0.lnk")));
    }
}
