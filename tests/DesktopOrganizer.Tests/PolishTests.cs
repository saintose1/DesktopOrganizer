using DesktopOrganizer.App.Desktop;
using DesktopOrganizer.App.ViewModels;
using DesktopOrganizer.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopOrganizer.Tests;

public sealed class PolishTests
{
    [Fact]
    public void HoverLeaseIsDueImmediatelyAndTreatsBodyAndIconsAsInside()
    {
        var hover = new FenceHoverState();
        hover.Observe(true, true, true, true, false, TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, FenceHoverState.ExpandDelay);
        Assert.Equal(TimeSpan.Zero, FenceHoverState.CollapseDelay);
        Assert.True(hover.Due(TimeSpan.Zero));
        hover.Observe(true, true, true, true, false, TimeSpan.FromMilliseconds(49));
        Assert.Equal(TimeSpan.Zero, hover.DueAt); // Repeated motion keeps the original deadline.
        hover.Presented(true);
        hover.Observe(true, true, true, false, false, TimeSpan.FromSeconds(1));
        Assert.Null(hover.DueAt); // Body/Explorer icons need no HWND mouse-enter.
        hover.Observe(true, true, false, false, false, TimeSpan.FromSeconds(2));
        Assert.False(hover.Due(TimeSpan.FromSeconds(2)));
    }
    [Fact]
    public void ManualExpandedFenceNeverGetsHoverLease()
    {
        var hover = new FenceHoverState();
        hover.Observe(true, false, true, true, false, TimeSpan.Zero);
        hover.Observe(true, false, false, false, false, TimeSpan.FromSeconds(1));
        Assert.False(hover.Expanded); Assert.Null(hover.DueAt);
    }
    [Fact]
    public void ReentryAndGesturesCancelQueuedTransitions()
    {
        var hover = new FenceHoverState();
        hover.Observe(true, true, true, true, false, TimeSpan.Zero);
        hover.Observe(true, true, false, false, false, TimeSpan.FromMilliseconds(20));
        Assert.Null(hover.DueAt);
        hover.Presented(true);
        hover.Observe(true, true, false, false, false, TimeSpan.FromSeconds(1));
        hover.Observe(true, true, true, false, false, TimeSpan.FromMilliseconds(1020));
        Assert.Null(hover.DueAt);
        hover.Observe(true, true, false, false, true, TimeSpan.FromSeconds(2));
        Assert.Null(hover.DueAt); Assert.True(hover.Expanded);
        hover.Observe(true, true, false, false, false, TimeSpan.FromSeconds(3));
        Assert.False(hover.Due(TimeSpan.FromSeconds(3)));
    }
    [Fact]
    public void ManualButtonSuppressesHoverUntilLeaveAndDisablingRestoresManualState()
    {
        var hover = new FenceHoverState();
        hover.ManualAction(); hover.Presented(false);
        hover.Observe(true, true, true, true, false, TimeSpan.Zero);
        Assert.Null(hover.DueAt);
        hover.Observe(true, true, false, false, false, TimeSpan.FromSeconds(1));
        hover.Observe(true, true, true, true, false, TimeSpan.FromSeconds(2));
        Assert.True(hover.Due(TimeSpan.FromSeconds(2)));
        hover.Presented(true);
        hover.Observe(false, true, true, true, false, TimeSpan.FromSeconds(3));
        Assert.False(hover.Due(TimeSpan.FromSeconds(3)));
    }
    [Theory]
    [InlineData("#000000")][InlineData("#FFFFFF")][InlineData("#CDAAFF")][InlineData("#182742")]
    [InlineData("#FF0000")][InlineData("#00FF00")][InlineData("#0000FF")][InlineData("#888888")]
    public void HsvRoundtripRetainsRgbExactly(string hex)
    {
        var hsv = HsvColor.FromHex(hex);
        Assert.Equal(hex, HsvColor.ToHex(hsv.Hue, hsv.Saturation, hsv.Value));
    }
    [Fact]
    public void PickerPublishesLiveColorOnceAndInvalidHexLeavesPreviewIntact()
    {
        var picker = new ColorPickerViewModel();
        var changes = new List<string>(); picker.ColorChanged += changes.Add;
        picker.LoadColor("#FF0000"); Assert.Empty(changes);
        picker.Hue = 120; Assert.Equal(["#00FF00"], changes);
        picker.Hex = "#bad"; Assert.NotEmpty(picker.Error); Assert.Equal("#00FF00", picker.Color);
        Assert.Single(changes);
        picker.Hex = "#ad8fe8"; Assert.Equal("#AD8FE8", picker.Color); Assert.Equal(2, changes.Count);
        picker.SetSaturationValue(1, 0); Assert.Equal("#000000", picker.Color); Assert.Equal(3, changes.Count);
    }
    [Fact]
    public void PreferencesRoundtripAndCorruptionPreservesOriginalFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DesktopOrganizer-polish-" + Guid.NewGuid().ToString("N"));
        var store = new JsonAppPreferences(directory, NullLogger<JsonAppPreferences>.Instance);
        try
        {
            Assert.False(store.Load().AutoExpandOnHover);
            store.Save(new(true));
            Assert.True(new JsonAppPreferences(directory, NullLogger<JsonAppPreferences>.Instance).Load().AutoExpandOnHover);
            Assert.Single(Directory.GetFiles(directory));
            File.WriteAllText(Path.Combine(directory, "settings.json"), "broken");
            Assert.Throws<System.Text.Json.JsonException>(() => store.Load());
            Assert.Throws<IOException>(() => store.Save(new(false)));
            Assert.Equal("broken", File.ReadAllText(Path.Combine(directory, "settings.json")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
    [Fact]
    public void SettingsSavesBeforePublishingAndFailedWriteRetainsPriorValue()
    {
        var store = new PreferencesFake();
        var vm = new SettingsViewModel(new StartupFake(), NullLogger<SettingsViewModel>.Instance, store);
        vm.AutoExpandOnHover = true; Assert.True(vm.AutoExpandOnHover); Assert.True(store.Current.AutoExpandOnHover);
        store.Fail = true; vm.AutoExpandOnHover = false;
        Assert.True(vm.AutoExpandOnHover); Assert.True(vm.HasError);
        store.Fail = false; store.Current = new(false); vm.Refresh();
        Assert.False(vm.AutoExpandOnHover);
    }
    private sealed class PreferencesFake : IAppPreferencesStore
    {
        public AppPreferences Current = new(); public bool Fail;
        public AppPreferences Load() => Current;
        public void Save(AppPreferences preferences) { if (Fail) throw new IOException("denied"); Current = preferences; }
    }
    private sealed class StartupFake : IStartupRegistration
    { public bool IsEnabled() => false; public void SetEnabled(bool enabled) { } }
}
