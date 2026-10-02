namespace DesktopOrganizer.Tests;

public sealed class TestDirectory : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DesktopOrganizer.Tests"));
    public string DirectoryPath { get; }

    public TestDirectory()
    {
        DirectoryPath = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    public void Dispose()
    {
        var resolvedPath = Path.GetFullPath(DirectoryPath);
        if (!string.Equals(Path.GetDirectoryName(resolvedPath), _root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its temporary root.");
        Directory.Delete(resolvedPath, recursive: true);
    }
}
