using DesktopOrganizer.App.ViewModels;

namespace DesktopOrganizer.Tests;

public sealed class TrayLifetimeTests
{
    [Fact]
    public void RepeatedCloseAndOpenOnlyHidesAndShowsExistingWindow()
    {
        var shown = 0; var hidden = 0; var exited = 0;
        var model = new TrayLifetimeViewModel(() => shown++, () => hidden++, () => Task.CompletedTask,
            () => exited++, _ => throw new InvalidOperationException());
        for (var i = 0; i < 100; i++) { Assert.False(model.RequestWindowClose()); model.Open(); }
        Assert.Equal(100, shown); Assert.Equal(100, hidden); Assert.Equal(0, exited);
    }
    [Fact]
    public async Task ExitWaitsForCleanupAndCannotRunTwice()
    {
        var ready = new TaskCompletionSource(); var calls = 0; var exits = 0;
        var model = new TrayLifetimeViewModel(() => { }, () => { }, () => { calls++; return ready.Task; },
            () => exits++, _ => throw new InvalidOperationException());
        var first = model.ExitAsync();
        await model.ExitAsync();
        Assert.Equal(0, exits); Assert.False(model.RequestWindowClose());
        ready.SetResult(); await first; await model.ExitAsync();
        Assert.Equal(1, calls); Assert.Equal(1, exits); Assert.True(model.RequestWindowClose());
    }
    [Fact]
    public async Task FailedCleanupKeepsApplicationRunningAndAllowsRetry()
    {
        var attempts = 0; var errors = 0; var shown = 0; var exits = 0;
        var model = new TrayLifetimeViewModel(() => shown++, () => { },
            () => ++attempts == 1 ? Task.FromException(new IOException("restore failed")) : Task.CompletedTask,
            () => exits++, _ => errors++);
        await model.ExitAsync();
        Assert.False(model.IsExiting); Assert.False(model.IsPreparingExit);
        Assert.Equal(1, errors); Assert.Equal(1, shown); Assert.Equal(0, exits);
        await model.ExitAsync(); Assert.Equal(1, exits);
    }
    [Fact]
    public void WindowsSessionExitBypassesHide()
    {
        var hides = 0;
        var model = new TrayLifetimeViewModel(() => { }, () => hides++, () => Task.CompletedTask, () => { }, _ => { });
        model.AllowSessionExit(); Assert.True(model.RequestWindowClose()); Assert.Equal(0, hides);
    }
}
