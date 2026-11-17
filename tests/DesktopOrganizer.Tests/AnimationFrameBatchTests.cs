using DesktopOrganizer.Core.Desktop;

namespace DesktopOrganizer.Tests;

/// <summary>Per-frame Shell batches carry only the items that moved; the final frame carries all of them.</summary>
public sealed class AnimationFrameBatchTests
{
    private static readonly DesktopIconTarget[] Frame =
    [
        new(@"C:\D\a.lnk", new(10, 100)), new(@"C:\D\b.lnk", new(90, 100)), new(@"C:\D\c.lnk", new(-4096, -4096)),
    ];

    [Fact]
    public void IntermediateFrameSendsOnlyMovedItems()
    {
        var known = new Dictionary<string, DesktopPoint>(FenceIconLayout.IdentityComparer)
        { [@"C:\D\a.lnk"] = new(10, 96), [@"C:\D\b.lnk"] = new(90, 100), [@"c:\d\C.LNK"] = new(-4096, -4096) };
        var send = new List<DesktopIconTarget>();
        Assert.True(AnimationFrameBatch.Select(Frame, id => known.TryGetValue(id, out var p) ? p : null, final: false, send));
        Assert.Equal(@"C:\D\a.lnk", Assert.Single(send).StableId); // b settled, c still parked (identity is case-insensitive)
    }

    [Fact]
    public void UnknownItemsAreAlwaysSent()
    {
        var send = new List<DesktopIconTarget>();
        Assert.True(AnimationFrameBatch.Select(Frame, _ => null, final: false, send));
        Assert.Equal(3, send.Count);
    }

    [Fact]
    public void UnchangedIntermediateFrameSendsNothing()
    {
        var send = new List<DesktopIconTarget> { Frame[0] };
        Assert.False(AnimationFrameBatch.Select(Frame, id => Frame.First(t => t.StableId == id).Position, final: false, send));
        Assert.Empty(send);
    }

    [Fact]
    public void FinalFrameReassertsTheWholeSet()
    {
        var send = new List<DesktopIconTarget>();
        Assert.False(AnimationFrameBatch.Select(Frame, id => Frame.First(t => t.StableId == id).Position, final: true, send));
        Assert.Equal(Frame, send);
        Assert.True(AnimationFrameBatch.Select(Frame, _ => null, final: true, send));
        Assert.Equal(Frame, send);
    }
}
