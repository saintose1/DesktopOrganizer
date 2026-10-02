using DesktopOrganizer.App.Desktop;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Tests;

/// <summary>Application contract tests; no simulated Explorer HWND or paint loop.</summary>
public sealed class FenceVisualTransactionTests
{
    private static readonly Fence Before = new(Guid.NewGuid(), "Work", 100, 200, 450, 350);
    [Fact]
    public async Task PersistenceWaitsForCompletedIconAndSurfacePresentation()
    {
        var visual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var operation = FenceVisualTransaction.ExecuteAsync(Before, Before.ToggleCollapsed(), async _ =>
        { order.Add("present"); await visual.Task; order.Add("presented"); }, () =>
        { order.Add("persist"); return Task.FromResult(true); });
        Assert.Equal(["present"], order);
        visual.SetResult(); await operation;
        Assert.Equal(["present", "presented", "persist"], order);
    }
    [Fact]
    public async Task SlowPersistenceDoesNotDelayTheVisualTransition()
    {
        var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var presented = Before;
        var operation = FenceVisualTransaction.ExecuteAsync(Before, Before.ToggleCollapsed(), state =>
        { presented = state; return Task.CompletedTask; }, () => saved.Task);
        Assert.True(presented.IsCollapsed); Assert.False(operation.IsCompleted);
        saved.SetResult(true); await operation;
    }
    [Fact]
    public async Task FailedDurableCommitRestoresTheExpandedPresentation()
    {
        var presentations = new List<Fence>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => FenceVisualTransaction.ExecuteAsync(Before, Before.ToggleCollapsed(), state =>
        { presentations.Add(state); return Task.CompletedTask; }, () => Task.FromResult(false)));
        Assert.Equal([Before.ToggleCollapsed(), Before], presentations);
    }
    [Fact]
    public async Task FailedIconHideCannotCommitAndRestoresOriginalPresentation()
    {
        var committed = false; var presentations = new List<Fence>();
        await Assert.ThrowsAsync<IOException>(() => FenceVisualTransaction.ExecuteAsync(Before, Before.ToggleCollapsed(), state =>
        {
            presentations.Add(state);
            return state.IsCollapsed ? Task.FromException(new IOException("Shell unavailable")) : Task.CompletedTask;
        }, () => { committed = true; return Task.FromResult(true); }));
        Assert.False(committed); Assert.Equal([Before.ToggleCollapsed(), Before], presentations);
    }
    [Fact]
    public async Task PersistenceAndRollbackErrorsAreBothReported()
    {
        var failure = await Assert.ThrowsAsync<AggregateException>(() => FenceVisualTransaction.ExecuteAsync(Before, Before.ToggleCollapsed(), state =>
            state.IsCollapsed ? Task.CompletedTask : Task.FromException(new IOException("Restore failed")),
            () => Task.FromException<bool>(new IOException("Save failed"))));
        Assert.Equal(["Save failed", "Restore failed"], failure.InnerExceptions.Select(e => e.Message));
    }
    [Fact]
    public async Task ExpandUsesTheSamePresentationBeforePersistenceContract()
    {
        var before = Before.ToggleCollapsed(); var presented = before;
        await FenceVisualTransaction.ExecuteAsync(before, Before, state =>
        { presented = state; return Task.CompletedTask; }, () =>
        { Assert.False(presented.IsCollapsed); Assert.Equal(350, presented.Height); return Task.FromResult(true); });
    }
}
