using DesktopOrganizer.App.ViewModels;
using DesktopOrganizer.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopOrganizer.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void ToggleWritesRealServiceAndRefreshReadsExternalChanges()
    {
        var registration = new FakeStartup();
        var vm = new SettingsViewModel(registration, NullLogger<SettingsViewModel>.Instance);
        Assert.False(vm.IsStartupEnabled);
        vm.IsStartupEnabled = true;
        Assert.True(registration.Enabled);
        Assert.True(vm.IsStartupEnabled);
        registration.Enabled = false;
        vm.RefreshCommand.Execute(null);
        Assert.False(vm.IsStartupEnabled);
        Assert.Equal(1, registration.Writes);
    }

    [Fact]
    public void FailedWriteKeepsPreviousStateAndReportsError()
    {
        var registration = new FakeStartup { FailWrite = true };
        var vm = new SettingsViewModel(registration, NullLogger<SettingsViewModel>.Instance);
        var notified = false;
        vm.PropertyChanged += (_, args) => notified |= args.PropertyName == nameof(vm.IsStartupEnabled);
        vm.IsStartupEnabled = true;
        Assert.False(vm.IsStartupEnabled);
        Assert.True(vm.HasError);
        Assert.True(notified);
        registration.FailWrite = false;
        vm.IsStartupEnabled = true;
        Assert.True(vm.IsStartupEnabled);
        Assert.False(vm.HasError);
    }

    [Fact]
    public void ReadFailureIsVisibleAndCanRecover()
    {
        var registration = new FakeStartup { FailRead = true };
        var vm = new SettingsViewModel(registration, NullLogger<SettingsViewModel>.Instance);
        Assert.True(vm.HasError);
        registration.FailRead = false;
        registration.Enabled = true;
        vm.Refresh();
        Assert.True(vm.IsStartupEnabled);
        Assert.False(vm.HasError);
    }

    [Fact]
    public void StartupCommandQuotesSpacesAndStartsInTray()
    {
        Assert.Equal("\"C:\\Program Files\\DesktopOrganizer\\DesktopOrganizer.App.exe\" --StartInTray=true",
            WindowsStartupRegistration.CreateCommand(@"C:\Program Files\DesktopOrganizer\DesktopOrganizer.App.exe"));
    }

    [Theory]
    [InlineData("relative.exe")]
    [InlineData("C:\\app.dll")]
    [InlineData("C:\\bad\"name.exe")]
    [InlineData("C:\\bad\nname.exe")]
    public void UnsafeStartupCommandsAreRejected(string path) =>
        Assert.Throws<ArgumentException>(() => WindowsStartupRegistration.CreateCommand(path));

    [Fact]
    public void OverlongRunCommandIsRejected() => Assert.Throws<ArgumentException>(() =>
        WindowsStartupRegistration.CreateCommand("C:\\" + new string('a', 250) + ".exe"));

    private sealed class FakeStartup : IStartupRegistration
    {
        public bool Enabled, FailWrite, FailRead;
        public int Writes;
        public bool IsEnabled() => FailRead ? throw new IOException("read denied") : Enabled;
        public void SetEnabled(bool enabled)
        {
            if (FailWrite) throw new IOException("write denied");
            Enabled = enabled; Writes++;
        }
    }
}
