using DesktopOrganizer.App.Desktop;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopOrganizer.Tests;

/// <summary>Instant-expand mode (default) versus "always hidden" for items of collapsed groups.</summary>
public sealed class ConcealmentPolicyTests
{
    [Fact]
    public void InstantModeHidesOnlyForADragAndNeverWhileIdleOrOnAClick()
    {
        Assert.False(ConcealmentPolicy.HideOnPress(concealWhenIdle: false));
        Assert.True(ConcealmentPolicy.HideOnDragStart(concealWhenIdle: false));
        Assert.False(ConcealmentPolicy.HideWhenIdle(concealWhenIdle: false));
    }

    [Fact]
    public void AlwaysHiddenModeKeepsThePreviousBehaviour()
    {
        Assert.True(ConcealmentPolicy.HideOnPress(concealWhenIdle: true));
        Assert.False(ConcealmentPolicy.HideOnDragStart(concealWhenIdle: true)); // already hidden at the press
        Assert.True(ConcealmentPolicy.HideWhenIdle(concealWhenIdle: true));
        Assert.False(ConcealmentPolicy.ReturnToParking(true, false, TimeSpan.FromHours(1)));
    }

    [Theory]
    [InlineData(false, 0.0, false)]     // just after a drop: a follow-up drag is likely
    [InlineData(false, 1.4, false)]
    [InlineData(false, 1.5, true)]
    [InlineData(true, 10.0, false)]     // Explorer still busy (button down, capture, drag)
    public void HiddenItemsReturnToParkingOnlyWhenTheDesktopIsQuiet(bool explorerBusy, double secondsSinceGesture, bool expected)
        => Assert.Equal(expected, ConcealmentPolicy.ReturnToParking(false, explorerBusy, TimeSpan.FromSeconds(secondsSinceGesture)));

    [Fact]
    public void PreferenceDefaultsToInstantExpandAndRoundTrips()
    {
        using var directory = new TestDirectory();
        var store = new JsonAppPreferences(directory.DirectoryPath, NullLogger<JsonAppPreferences>.Instance);
        Assert.False(store.Load().ConcealCollapsedItems);
        store.Save(new AppPreferences(true, 0.2, 0.2, "ru", AppPreferences.DarkTheme, concealCollapsedItems: true));
        var loaded = store.Load();
        Assert.True(loaded.ConcealCollapsedItems);
        Assert.Equal(AppPreferences.DarkTheme, loaded.Theme);
        File.WriteAllText(Path.Combine(directory.DirectoryPath, "settings.json"), """{"AutoExpandOnHover":true,"ExpandAnimationSeconds":0.2,"CollapseAnimationSeconds":0.2}""");
        Assert.False(store.Load().ConcealCollapsedItems); // files from 0.3.x: instant expand
    }

    [Fact]
    public void BothDirectionsMoveNoticeablyInTheFirstFrames()
    {
        // At 240 Hz the first frame lands near 5% of a 0.2 s transition: both directions must already move.
        Assert.True(FenceTransitionAnimator.EasedProgress(0.05, expanding: true) > 0.12);
        Assert.True(FenceTransitionAnimator.EasedProgress(0.05, expanding: false) > 0.08);
        Assert.True(FenceTransitionAnimator.EasedProgress(0.25, expanding: false) > 0.4);
        foreach (var expanding in new[] { true, false })
        {
            Assert.Equal(0, FenceTransitionAnimator.EasedProgress(0, expanding));
            Assert.Equal(1, FenceTransitionAnimator.EasedProgress(1, expanding));
            var previous = 0.0;
            for (var i = 1; i <= 100; i++)
            {
                var value = FenceTransitionAnimator.EasedProgress(i / 100.0, expanding);
                Assert.True(value >= previous && value <= 1, "monotonic, no overshoot");
                previous = value;
            }
        }
    }
}
