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
    public async Task SuccessfulDropUsesOneBulkPositioningBeforeOneSave()
    {
        var transport = new RecordingTransport(); var saves = 0;
        await new IconLayoutTransaction(transport).ApplyAsync([new("A", new(200, 200))], [], () => { saves++; return true; }, TestContext.Current.CancellationToken);
        Assert.Equal(1, transport.DropCalls); Assert.Equal(1, transport.PositionCalls); Assert.Equal(1, saves);
    }
    [Fact]
    public async Task RemovingAssignmentsWithNoLayoutChangesDoesNotPositionOrRedrawAgain()
    {
        var transport = new RecordingTransport(); var saves = 0;
        await new IconLayoutTransaction(transport).ApplyAsync([], [], () => { saves++; return true; }, TestContext.Current.CancellationToken);
        Assert.Equal(0, transport.PositionCalls); Assert.Equal(0, transport.DropCalls);
        Assert.Equal(["read"], transport.Operations); Assert.Equal(1, saves);
    }
    [Fact]
    public async Task CompetingExplorerWriteRejectsDropWithoutASecondVisualWriteOrSave()
    {
        var transport = new RecordingTransport { OverwriteFirst = true }; var saved = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new IconLayoutTransaction(transport).ApplyAsync(
            [new("A", new(200, 200))], [], () => saved = true, TestContext.Current.CancellationToken));
        Assert.Equal(1, transport.PositionCalls); Assert.False(saved);
    }
    [Fact]
    public async Task DelayedReadbackDoesNotRepeatBulkPositioning()
    {
        var transport = new RecordingTransport { StaleFirstRead = true };
        await new IconLayoutTransaction(transport).ApplyAsync([new("A", new(200, 200))], [], () => true, TestContext.Current.CancellationToken);
        Assert.Equal(["position", "read", "read"], transport.Operations); Assert.Equal(1, transport.DropCalls);
    }
    [Fact]
    public async Task DeferredPersistenceStartsAfterVisualPositionAndReadback()
    {
        var transport = new RecordingTransport(); var accepted = new TaskCompletionSource<bool>();
        var transaction = new IconLayoutTransaction(transport).ApplyDeferredAsync([new("A", new(200, 200))], [], () => accepted.Task,
            TestContext.Current.CancellationToken);
        Assert.Equal(["position", "read"], transport.Operations); Assert.False(transaction.IsCompleted);
        accepted.SetResult(true); await transaction; Assert.Equal(1, transport.PositionCalls);
    }
    [Fact]
    public async Task PlannedDropUsesNativeLeaseAndOnlyPersistsAfterItsRedrawCompletion()
    {
        var transport = new PlannedTransport();
        await new IconLayoutTransaction(transport).ApplyPlannedAsync(snapshot =>
        {
            Assert.False(transport.Frozen); Assert.Equal(new DesktopPoint(10, 10), snapshot.Icons[0].Position);
            transport.Operations.Add("layout"); return [new("A", new(200, 200))];
        }, verified =>
        {
            Assert.False(transport.Frozen); Assert.Equal(new DesktopPoint(200, 200), verified.Icons[0].Position);
            transport.Operations.Add("persist"); return Task.FromResult(true);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(["read", "layout", "freeze", "position", "redraw", "read", "persist"], transport.Operations);
        Assert.Equal(1, transport.PlannedCalls); Assert.Equal(1, transport.PositionCalls); Assert.Equal(0, transport.DropCalls);
    }
    [Fact]
    public async Task PlannedAssignmentOnlyDropCanCommitWithoutAPositionOrRedrawLease()
    {
        var transport = new PlannedTransport(); var saves = 0;
        await new IconLayoutTransaction(transport).ApplyPlannedAsync(snapshot =>
        {
            Assert.False(transport.Frozen); transport.Operations.Add("layout"); return [];
        }, verified => { saves++; transport.Operations.Add("persist"); return Task.FromResult(true); }, TestContext.Current.CancellationToken);
        Assert.Equal(["read", "layout", "read", "persist"], transport.Operations);
        Assert.Equal(0, transport.PositionCalls); Assert.Equal(1, saves);
    }
    [Fact]
    public async Task PlannedCommitFailureRollsBackToPositionsFromNativeFinalSnapshot()
    {
        var transport = new PlannedTransport();
        await Assert.ThrowsAsync<IOException>(() => new IconLayoutTransaction(transport).ApplyPlannedAsync(
            _ => [new("A", new(200, 200))], _ => throw new IOException("disk full"), TestContext.Current.CancellationToken));
        Assert.False(transport.Frozen); Assert.Equal(new DesktopPoint(10, 10), transport.Position);
        Assert.Equal(1, transport.PlannedCalls); Assert.Equal(2, transport.PositionCalls);
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
    private class RecordingTransport : IDesktopIconService, IDesktopDropPositioner
    {
        public List<string> Operations { get; } = [];
        public DesktopPoint Position { get; private set; } = new(10, 10);
        public int PositionCalls { get; private set; }
        public int DropCalls { get; private set; }
        public bool OverwriteFirst { get; init; }
        public bool FailFirst { get; init; }
        public bool FailSecond { get; init; }
        public bool StaleFirstRead { get; init; }
        private int _reads;
        public Task PositionDropAsync(IReadOnlyList<DesktopIconTarget> targets, CancellationToken cancellationToken = default)
        { DropCalls++; return PositionAsync(targets, cancellationToken); }
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
            var position = StaleFirstRead && ++_reads == 1 ? new DesktopPoint(10, 10) : Position;
            return Task.FromResult(new DesktopIconSnapshot(true, true, [new(0, "A", "A", position, position, new(position.X, position.Y, 90, 100))], IconGridMetrics.Default));
        }
    }
    private sealed class PlannedTransport : RecordingTransport, IDesktopDropReconciler
    {
        public bool Frozen { get; private set; }
        public int PlannedCalls { get; private set; }
        public async Task ReconcileDropAsync(Func<DesktopIconSnapshot, IReadOnlyList<DesktopIconTarget>> layout, CancellationToken cancellationToken = default)
        {
            PlannedCalls++;
            var targets = layout(await ReadAsync(cancellationToken));
            if (targets.Count == 0) return;
            Frozen = true; Operations.Add("freeze");
            try { await PositionAsync(targets, cancellationToken); }
            finally { Frozen = false; Operations.Add("redraw"); }
        }
    }
}
