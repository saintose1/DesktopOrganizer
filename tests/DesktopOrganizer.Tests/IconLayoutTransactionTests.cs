using DesktopOrganizer.App.Desktop;
using DesktopOrganizer.Core.Desktop;

namespace DesktopOrganizer.Tests;

/// <summary>Contract/order/failure tests, not simulations of Windows Explorer.</summary>
public sealed class IconLayoutTransactionTests
{
    [Fact]
    public async Task PositionsAndReadbackPrecedeDurableAssignment()
    {
        var transport = new RecordingTransport(); var committed = false;
        await new IconLayoutTransaction(transport).ApplyAsync([new("A", new(200, 200))], [new("A", new(10, 10))], () =>
        { Assert.Equal(new DesktopPoint(200, 200), transport.Position); transport.Operations.Add("persist"); committed = true; return true; }, TestContext.Current.CancellationToken);
        Assert.True(committed); Assert.Equal(["position", "read", "persist"], transport.Operations);
    }
    [Fact]
    public async Task ExplorerFinalWriteIsReconciledBeforeCommit()
    {
        var transport = new RecordingTransport { OverwriteFirst = true };
        await new IconLayoutTransaction(transport).ApplyAsync([new("A", new(200, 200))], [], () =>
        { Assert.Equal(2, transport.PositionCalls); return true; }, TestContext.Current.CancellationToken);
        Assert.Equal(new DesktopPoint(200, 200), transport.Position);
    }
    [Fact]
    public async Task PersistenceFailureRestoresOriginalExplorerPosition()
    {
        var transport = new RecordingTransport();
        await Assert.ThrowsAsync<IOException>(() => new IconLayoutTransaction(transport).ApplyAsync([new("A", new(200, 200))], [new("A", new(10, 10))],
            () => throw new IOException("disk full"), TestContext.Current.CancellationToken));
        Assert.Equal(new DesktopPoint(10, 10), transport.Position);
    }
    [Fact]
    public async Task ConcurrentModelRevisionRejectsAndRollsBackProposal()
    {
        var transport = new RecordingTransport();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new IconLayoutTransaction(transport).ApplyAsync([new("A", new(200, 200))], [new("A", new(10, 10))], () => false, TestContext.Current.CancellationToken));
        Assert.Equal(new DesktopPoint(10, 10), transport.Position);
    }
    [Fact]
    public async Task PositioningFailureCannotPersistAssignment()
    {
        var transport = new RecordingTransport { FailFirst = true }; var committed = false;
        await Assert.ThrowsAsync<IOException>(() => new IconLayoutTransaction(transport).ApplyAsync([new("A", new(200, 200))], [new("A", new(10, 10))], () => committed = true, TestContext.Current.CancellationToken));
        Assert.False(committed); Assert.Equal(new DesktopPoint(10, 10), transport.Position);
    }
    [Fact]
    public async Task FailedRollbackReportsBothOriginalFailureAndRecoveryFailure()
    {
        var transport = new RecordingTransport { FailSecond = true };
        var failure = await Assert.ThrowsAsync<AggregateException>(() => new IconLayoutTransaction(transport).ApplyAsync(
            [new("A", new(200, 200))], [new("A", new(10, 10))], () => throw new IOException("disk full"), TestContext.Current.CancellationToken));
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Equal("disk full", failure.InnerExceptions[0].Message);
        Assert.Equal("transport failed", failure.InnerExceptions[1].Message);
    }
    private sealed class RecordingTransport : IDesktopIconService
    {
        public List<string> Operations { get; } = [];
        public DesktopPoint Position { get; private set; } = new(10, 10);
        public int PositionCalls { get; private set; }
        public bool OverwriteFirst { get; init; }
        public bool FailFirst { get; init; }
        public bool FailSecond { get; init; }
        public Task PositionAsync(IReadOnlyList<DesktopIconTarget> targets, CancellationToken cancellationToken = default)
        {
            Operations.Add("position"); PositionCalls++;
            if ((FailFirst && PositionCalls == 1) || (FailSecond && PositionCalls == 2)) throw new IOException("transport failed");
            Position = OverwriteFirst && PositionCalls == 1 ? new(250, 250) : targets[0].Position;
            return Task.CompletedTask;
        }
        public Task<DesktopIconSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            Operations.Add("read");
            return Task.FromResult(new DesktopIconSnapshot(true, true, [new(0, "A", "A", Position, Position, new(Position.X, Position.Y, 90, 100))], IconGridMetrics.Default));
        }
    }
}
