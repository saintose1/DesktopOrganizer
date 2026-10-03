using System.Windows.Threading;
using System.Diagnostics;
using DesktopOrganizer.App.Desktop;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Infrastructure.Interop;
using DesktopOrganizer.App.ViewModels;
using DesktopOrganizer.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopOrganizer.Tests;

public sealed class FenceAnimationTests
{
    [Theory]
    [InlineData(36, 650)][InlineData(650, 36)]
    public void SmoothHeightHasExactEndpointsNoOvershootAndMonotonicFrames(double from, double to)
    {
        Assert.Equal(from, FenceTransitionAnimator.HeightAt(from, to, 0));
        Assert.Equal(to, FenceTransitionAnimator.HeightAt(from, to, 1));
        var previous = from;
        for (var i = 1; i <= 100; i++)
        {
            var height = FenceTransitionAnimator.HeightAt(from, to, i / 100.0);
            Assert.InRange(height, Math.Min(from, to), Math.Max(from, to));
            Assert.True(to > from ? height >= previous : height <= previous);
            previous = height;
        }
        var startDistance = Math.Abs(FenceTransitionAnimator.HeightAt(from, to, 0.01) - from);
        Assert.True(to > from ? startDistance > Math.Abs(to - from) * 0.01 : startDistance < Math.Abs(to - from) * 0.01);
        Assert.True(Math.Abs(to - FenceTransitionAnimator.HeightAt(from, to, 0.99)) < Math.Abs(to - from) * 0.01);
    }
    [Theory]
    [InlineData(-0.1)][InlineData(3.01)][InlineData(double.NaN)][InlineData(double.PositiveInfinity)]
    public void InvalidDurationsCannotEnterPreferences(double value)
    {
        Assert.Throws<ArgumentException>(() => new AppPreferences(false, value, 0.1));
        Assert.Throws<ArgumentException>(() => new AppPreferences(false, 0.1, value));
    }
    [Fact]
    public void LegacyPreferencesGetDefaultsAndDurationsRoundtrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DesktopOrganizer-animation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new JsonAppPreferences(directory, NullLogger<JsonAppPreferences>.Instance);
            File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"AutoExpandOnHover\":true}");
            Assert.Equal(new AppPreferences(true), store.Load());
            var preferences = new AppPreferences(true, 0, 2.5);
            store.Save(preferences); Assert.Equal(preferences, store.Load());
            File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"ExpandAnimationSeconds\":-1}");
            Assert.Throws<ArgumentException>(() => store.Load());
            Assert.Throws<IOException>(() => store.Save(new()));
            Assert.Contains("-1", File.ReadAllText(Path.Combine(directory, "settings.json")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    [Fact]
    public void SettingsKeepAllPreferencesAndRevertFailedOrInvalidDurationChanges()
    {
        var store = new PreferenceStore();
        var vm = new SettingsViewModel(new Startup(), NullLogger<SettingsViewModel>.Instance, store);
        vm.ExpandAnimationSeconds = 0.4; vm.CollapseAnimationSeconds = 0.25; vm.AutoExpandOnHover = true;
        Assert.Equal(new AppPreferences(true, 0.4, 0.25), store.Current);
        var restarted = new SettingsViewModel(new Startup(), NullLogger<SettingsViewModel>.Instance, store);
        Assert.Equal(0.4, restarted.ExpandAnimationSeconds); Assert.Equal(0.25, restarted.CollapseAnimationSeconds);
        store.Fail = true; vm.CollapseAnimationSeconds = 0.9;
        Assert.Equal(0.25, vm.CollapseAnimationSeconds); Assert.True(vm.HasError);
        store.Fail = false; vm.ExpandAnimationSeconds = -1;
        Assert.Equal(0.4, vm.ExpandAnimationSeconds); Assert.True(vm.HasError);
        Assert.Equal(new AppPreferences(true, 0.4, 0.25), store.Current);
        vm.ExpandAnimationSeconds = 0; Assert.False(vm.HasError); Assert.Equal(0, store.Current.ExpandAnimationSeconds);
    }
    [Fact]
    public Task AnimatorCompletesOnDispatcherAndZeroDurationIsImmediate() => OnDispatcher(async () =>
    {
        var clock = new ManualClock();
        using var animator = new FenceTransitionAnimator(clock);
        var frames = new List<double>();
        var animation = animator.AnimateAsync(36, 500, 0.08, frames.Add);
        Assert.False(animation.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => animator.AnimateAsync(36, 500, 0.1, frames.Add));
        await Task.Delay(100); clock.Pulse();
        await animation;
        Assert.NotEmpty(frames); Assert.Equal(500, frames[^1]);
        frames.Clear();
        var immediate = animator.AnimateAsync(500, 36, 0, frames.Add);
        Assert.True(immediate.IsCompletedSuccessfully); Assert.Equal([36.0], frames);
    });
    [Fact]
    public Task DisposalCancelsPendingFramesAndRepaintFailureReachesCaller() => OnDispatcher(async () =>
    {
        var clock = new ManualClock();
        var animator = new FenceTransitionAnimator(clock);
        var animation = animator.AnimateAsync(36, 500, 3, _ => { });
        animator.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => animation);
        using var failing = new FenceTransitionAnimator(clock);
        var failed = failing.AnimateAsync(36, 500, 0.05, _ => throw new IOException("surface lost"));
        clock.Pulse(); await Assert.ThrowsAsync<IOException>(() => failed);
    });
    [Fact]
    public Task CompositionPulsesCoalesceToOneLatestFollowupAfterPresenting() => OnDispatcher(async () =>
    {
        var clock = new ManualClock();
        using var animator = new FenceTransitionAnimator(clock);
        var count = 0;
        var animation = animator.AnimateAsync(36, 650, 0.15, _ =>
        {
            count++;
            if (count != 1) return;
            for (var i = 0; i < 25; i++) clock.Pulse(); // A burst during the first native commit.
            Assert.Equal(1, count); // No reentrancy while the previous frame is executing.
        });
        for (var i = 0; i < 100; i++) clock.Pulse();
        await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.Equal(2, count); // Exactly one latest frame follows the busy frame, not 25 queued frames.
        await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.Equal(2, count); // No busy loop in the absence of another compositor pulse.
        await Task.Delay(170); clock.Pulse(); await animation;
        Assert.Equal(3, count);
        Assert.Equal(3, animator.LastMetrics.Frames);
        Assert.Equal(124, animator.LastMetrics.CoalescedFrames);
        Assert.Null(clock.Frame);
    });
    [Fact]
    public Task ClockFailureAndLateCallbacksCannotMutateCompletedOrDisposedTransitions() => OnDispatcher(async () =>
    {
        var clock = new ManualClock(); var calls = 0;
        var animator = new FenceTransitionAnimator(clock);
        var animation = animator.AnimateAsync(36, 500, 1, _ => calls++);
        var late = clock.Frame!;
        clock.Fail(new IOException("composition unavailable"));
        await Assert.ThrowsAsync<IOException>(() => animation);
        late(Stopwatch.GetTimestamp()); await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.Equal(0, calls);
        var next = animator.AnimateAsync(500, 36, 1, _ => calls++);
        late(Stopwatch.GetTimestamp()); await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.Equal(0, calls);
        animator.Dispose(); clock.Pulse();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        Assert.Equal(0, calls);
    });
    [Theory]
    [InlineData(-103.45, -50.25, 1.0)][InlineData(120.45, 80.25, 1.25)][InlineData(120.45, 80.25, 1.5)]
    public void HeightProjectionUsesAbsolutePixelsAndMatchesDesktopClipping(double x, double y, double dpi)
    {
        var fence = new Fence(Guid.NewGuid(), "Fractional geometry", x, y, 450.3, 650.7);
        var area = new DesktopArea(-1920, 0, 4480, 1440);
        var projection = new FenceHeightAnimationGeometry(fence, area);
        var header = FenceInteractionGeometry.VisibleHeight(fence.ToggleCollapsed(), dpi);
        var previous = 0;
        for (var frame = 0; frame <= 100; frame++)
        {
            var height = FenceTransitionAnimator.HeightAt(header, fence.Height, frame / 100.0);
            var actual = projection.At(height);
            Assert.Equal(FenceSurfaceLayout.Clip(fence.WithBounds(x, y, fence.Width, height), area, dpi), actual);
            if (actual is { } layout) { Assert.True(layout.Height >= previous); previous = layout.Height; }
        }
        Assert.Equal(FenceSurfaceLayout.Clip(fence.ToggleCollapsed(), area, dpi), projection.At(header));
        Assert.Equal(650.7, fence.Height);
    }
    private static async Task OnDispatcher(Func<Task> test)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); completed.SetResult(); }
                catch (Exception error) { completed.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        thread.Join(TimeSpan.FromSeconds(1));
    }
    [Fact]
    public Task AsyncVisualCommitKeepsOnlyOneFrameInFlightAndUsesTheSameEasedHeight() => OnDispatcher(async () =>
    {
        var clock = new ManualClock(); using var animator = new FenceTransitionAnimator(clock);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var frames = new List<FenceAnimationFrame>();
        var animation = animator.AnimateFramesAsync(36, 650, 0.1, async frame =>
        { calls++; frames.Add(frame); if (calls == 1) await gate.Task; });
        clock.Pulse(); await Dispatcher.Yield(DispatcherPriority.Background);
        for (var i = 0; i < 100; i++) clock.Pulse();
        await Task.Delay(130);
        Assert.Equal(1, calls); Assert.False(animation.IsCompleted);
        gate.SetResult(); await Dispatcher.Yield(DispatcherPriority.Background);
        clock.Pulse(); await animation;
        Assert.Equal(2, calls); Assert.Equal(100, animator.LastMetrics.CoalescedFrames);
        Assert.All(frames, f => Assert.Equal(36 + (650 - 36) * f.EasedProgress, f.Height));
        Assert.Equal(new FenceAnimationFrame(1, 1, 650), frames[^1]);
    });
    [Fact]
    public Task AsyncFrameFailureStopsClockBeforeFurtherPositioning() => OnDispatcher(async () =>
    {
        var clock = new ManualClock(); using var animator = new FenceTransitionAnimator(clock);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var animation = animator.AnimateFramesAsync(36, 650, 1, _ => { calls++; return gate.Task; });
        clock.Pulse(); await Dispatcher.Yield(DispatcherPriority.Background);
        clock.Pulse(); clock.Pulse();
        gate.SetException(new IOException("Shell frame failed."));
        await Assert.ThrowsAsync<IOException>(() => animation);
        clock.Pulse(); await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.Equal(1, calls); Assert.Null(clock.Frame); Assert.Equal(2, animator.LastMetrics.CoalescedFrames);
    });
    [Fact]
    public Task ClockFailureWaitsForInFlightShellFrameBeforeRollbackCanStart() => OnDispatcher(async () =>
    {
        var clock = new ManualClock(); using var animator = new FenceTransitionAnimator(clock);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var animation = animator.AnimateFramesAsync(36, 650, 1, _ => { calls++; return gate.Task; });
        clock.Pulse(); await Dispatcher.Yield(DispatcherPriority.Background);
        clock.Fail(new IOException("DWM clock lost.")); await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.False(animation.IsCompleted); Assert.Null(clock.Frame); Assert.Equal(1, calls);
        gate.SetResult(); await Assert.ThrowsAsync<IOException>(() => animation);
        clock.Pulse(); await Dispatcher.Yield(DispatcherPriority.Background); Assert.Equal(1, calls);
    });
    private sealed class PreferenceStore : IAppPreferencesStore
    {
        public AppPreferences Current = new(); public bool Fail;
        public AppPreferences Load() => Current;
        public void Save(AppPreferences preferences) { if (Fail) throw new IOException("denied"); Current = preferences; }
    }
    private sealed class Startup : IStartupRegistration
    { public bool IsEnabled() => false; public void SetEnabled(bool enabled) { } }
    private sealed class ManualClock : ICompositionFrameClock
    {
        public Action<long>? Frame;
        private Action<Exception>? _failed;
        public IDisposable Subscribe(Action<long> frame, Action<Exception> failed)
        { Frame = frame; _failed = failed; return new Subscription(this); }
        public void Pulse() => Frame?.Invoke(Stopwatch.GetTimestamp());
        public void Fail(Exception error) => _failed?.Invoke(error);
        private sealed class Subscription(ManualClock clock) : IDisposable
        { public void Dispose() { clock.Frame = null; clock._failed = null; } }
    }
}
