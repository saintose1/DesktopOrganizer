using DesktopOrganizer.Infrastructure;

namespace DesktopOrganizer.Tests;

public sealed class ShortcutFeaturesTests
{
    [Fact]
    public void SearchMatchesAllWordsAndRanksThePrefixFirst()
    {
        ProgramShortcut[] items =
        [
            new("Legacy WinSCP Client", "legacy.lnk"),
            new("WinSCP Client", "winscp.lnk"),
            new("WinSCP Manual", "help.lnk"),
            new("Калькулятор", "calculator.lnk")
        ];
        Assert.Equal(["winscp.lnk", "legacy.lnk"], ProgramShortcutSearch.Match(items, "winscp client").Select(x => x.Path));
        Assert.Equal("calculator.lnk", Assert.Single(ProgramShortcutSearch.Match(items, "КАЛЬК")).Path);
        Assert.Empty(ProgramShortcutSearch.Match(items, "Vortex"));
    }

    [Fact]
    public void MoveKeepsIdenticalOriginalInTheBackupAndCommitsPersonalPath()
    {
        using var root = new TestDirectory();
        var (migration, shared, personal) = Arrange(root.DirectoryPath);
        string? assigned = null;
        Assert.True(migration.Move(shared, path => { assigned = path; return true; }));
        Assert.Equal(personal, assigned);
        Assert.False(File.Exists(shared));
        Assert.Equal("fixture", File.ReadAllText(personal));
        var original = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(root.DirectoryPath, "shared-shortcut-backups"), "*.lnk", SearchOption.AllDirectories));
        Assert.Equal("fixture", File.ReadAllText(original));
    }

    [Fact]
    public void FailedSaveKeepsSharedShortcutAndRemovesOnlyTheNewCopy()
    {
        using var root = new TestDirectory();
        var (migration, shared, personal) = Arrange(root.DirectoryPath);
        Assert.False(migration.Move(shared, _ => false));
        Assert.Equal("fixture", File.ReadAllText(shared));
        Assert.False(File.Exists(personal));
    }

    [Fact]
    public void ExistingPersonalShortcutIsNeverOverwritten()
    {
        using var root = new TestDirectory();
        var (migration, shared, personal) = Arrange(root.DirectoryPath);
        File.WriteAllText(personal, "existing");
        var writes = 0;
        Assert.Throws<IOException>(() => migration.Move(shared, _ => { writes++; return true; }));
        Assert.Equal(0, writes);
        Assert.Equal("existing", File.ReadAllText(personal));
        Assert.Equal("fixture", File.ReadAllText(shared));
    }

    private static (SharedShortcutMigration Service, string Shared, string Personal) Arrange(string root)
    {
        var shared = Directory.CreateDirectory(Path.Combine(root, "shared")).FullName;
        var personal = Directory.CreateDirectory(Path.Combine(root, "personal")).FullName;
        var source = Path.Combine(shared, "Demo.lnk");
        File.WriteAllText(source, "fixture");
        return (new SharedShortcutMigration(shared, personal, root), source, Path.Combine(personal, "Demo.lnk"));
    }
}
