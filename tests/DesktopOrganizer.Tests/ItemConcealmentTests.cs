using DesktopOrganizer.Infrastructure.Interop;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopOrganizer.Tests;

/// <summary>Hidden-attribute journal on temporary files outside the desktop; Explorer presentation is not verified here.</summary>
public sealed class ItemConcealmentTests
{
    [Fact]
    public async Task ConcealJournalsOwnedFilesAndSkipsVirtualAndAlreadyHiddenItems()
    {
        var directory = Directory.CreateTempSubdirectory("DesktopOrganizer-conceal-").FullName;
        var file = Path.Combine(directory, "item.txt");
        var userHidden = Path.Combine(directory, "user-hidden.txt");
        var journal = Path.Combine(directory, "hidden-icons.json");
        File.WriteAllText(file, "x"); File.WriteAllText(userHidden, "x");
        File.SetAttributes(userHidden, File.GetAttributes(userHidden) | FileAttributes.Hidden);
        try
        {
            using (var service = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance, null, journal))
            {
                var hidden = await service.ConcealAsync([file, userHidden, "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"],
                    TestContext.Current.CancellationToken);
                if (hidden.Count == 0) return; // "Show hidden files" is enabled on this machine: concealment is intentionally off.
                Assert.Equal([file], hidden);
                Assert.True((File.GetAttributes(file) & FileAttributes.Hidden) != 0);
                Assert.Contains("item.txt", File.ReadAllText(journal));
                Assert.DoesNotContain("user-hidden", File.ReadAllText(journal)); // never take ownership of a user's hidden file
            }
            // A new process reads the journal: the item is still known as hidden by this application.
            using var restarted = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance, null, journal);
            Assert.Contains(file, restarted.Concealed);
        }
        finally
        {
            foreach (var path in new[] { file, userHidden }) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
    }
}
