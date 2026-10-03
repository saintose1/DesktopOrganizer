using System.Diagnostics;
using System.Windows.Threading;
using DesktopOrganizer.Infrastructure.Interop;

namespace DesktopOrganizer.App.Desktop;

/// <summary>Absolute-progress animation paced by desktop composition, with at most one pending Dispatcher frame.</summary>
public sealed class FenceTransitionAnimator(ICompositionFrameClock? clock = null) : IDisposable
{
    private readonly ICompositionFrameClock _clock = clock ?? new DesktopCompositionClock();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private Run? _active;
    public bool IsDisposed { get; private set; }
    public AnimationFrameMetrics LastMetrics { get; private set; }

    public Task AnimateAsync(double from, double to, double seconds, Action<double> present)
        => AnimateFramesAsync(from, to, seconds, frame => { present(frame.Height); return Task.CompletedTask; });
    public Task AnimateFramesAsync(double from, double to, double seconds, Func<FenceAnimationFrame, Task> present)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_active is not null) throw new InvalidOperationException("A Fence transition is already active.");
        if (!double.IsFinite(from) || !double.IsFinite(to) || !double.IsFinite(seconds) || seconds < 0)
            throw new ArgumentException("Invalid animation geometry or duration.");
        if (seconds == 0 || Math.Abs(from - to) < 0.5) { LastMetrics = default; return present(new(1, 1, to)); }
        var run = new Run(from, to, seconds, present);
        run.FrameAction = () => OnFrame(run);
        _active = run;
        try
        {
            run.Subscription = _clock.Subscribe(timestamp => QueueFrame(run, timestamp),
                error => _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => OnClockFailure(run, error))));
        }
        catch (Exception error) { Finish(run, error); }
        return run.Completion.Task;
    }
    private void QueueFrame(Run run, long timestamp)
    {
        if (Volatile.Read(ref run.Stopped) != 0) return;
        Interlocked.Exchange(ref run.LatestPulse, timestamp);
        TryQueueFrame(run, countCoalesced: true);
    }
    private void TryQueueFrame(Run run, bool countCoalesced)
    {
        if (Volatile.Read(ref run.Stopped) != 0) return;
        if (Interlocked.CompareExchange(ref run.Pending, 1, 0) != 0)
        { if (countCoalesced) Interlocked.Increment(ref run.Coalesced); return; }
        _dispatcher.BeginInvoke(DispatcherPriority.Render, run.FrameAction);
    }
    private async void OnFrame(Run run)
    {
        if (_active != run || IsDisposed) return;
        run.Executing = true;
        var consumedPulse = Interlocked.Read(ref run.LatestPulse);
        try
        {
            var now = Stopwatch.GetTimestamp();
            var interval = Stopwatch.GetElapsedTime(run.LastFrame, now).TotalMilliseconds;
            run.LastFrame = now; run.Intervals.Add(interval);
            var progress = Math.Min(1, Stopwatch.GetElapsedTime(run.Started, now).TotalSeconds / run.Seconds);
            var eased = EasedProgress(progress, run.To > run.From);
            await run.Present(new(progress, eased, run.From + (run.To - run.From) * eased));
            if (run.ClockFailure is { } failure) Finish(run, failure);
            else if (progress >= 1) Finish(run);
        }
        catch (Exception error) { Finish(run, error); }
        finally
        {
            run.Executing = false;
            Volatile.Write(ref run.Pending, 0);
            // Consume only the newest already-received compositor pulse. There is
            // still at most one queued/executing frame, without an extra refresh wait.
            if (_active == run && !IsDisposed && Interlocked.Read(ref run.LatestPulse) > consumedPulse)
                TryQueueFrame(run, countCoalesced: false);
        }
    }
    private void OnClockFailure(Run run, Exception error)
    {
        if (_active != run) return;
        run.ClockFailure = error; Volatile.Write(ref run.Stopped, 1); run.Subscription?.Dispose();
        // The caller must not dispose Shell/region leases or begin rollback while
        // a synchronous cross-process operation from the previous frame is in flight.
        if (!run.Executing) Finish(run, error);
    }
    private void Finish(Run run, Exception? error = null)
    {
        if (_active != run) return;
        Volatile.Write(ref run.Stopped, 1); run.Subscription?.Dispose(); _active = null;
        var elapsed = Stopwatch.GetElapsedTime(run.Started).TotalSeconds;
        var intervals = run.Intervals;
        var steady = intervals.Skip(1).Order().ToArray();
        var cadence = steady.Length == 0 ? 0 : steady[steady.Length / 2];
        var missed = cadence == 0 ? 0 : steady.Sum(ms => Math.Max(0, (int)Math.Round(ms / cadence) - 1));
        LastMetrics = new(intervals.Count, intervals.Count / elapsed,
            intervals.Count == 0 ? 0 : intervals.Average(), intervals.Count == 0 ? 0 : intervals.Max(),
            run.Coalesced, missed);
        if (error is null) run.Completion.TrySetResult(); else run.Completion.TrySetException(error);
    }
    public static double HeightAt(double from, double to, double progress)
    {
        if (!double.IsFinite(from) || !double.IsFinite(to) || !double.IsFinite(progress)) throw new ArgumentException("Invalid animation geometry.");
        return from + (to - from) * EasedProgress(progress, to > from);
    }
    public static double EasedProgress(double progress, bool expanding)
    {
        if (!double.IsFinite(progress)) throw new ArgumentException("Invalid animation progress.", nameof(progress));
        var t = Math.Clamp(progress, 0, 1);
        // Opening decelerates smoothly; closing starts/ends softly without bounce.
        return expanding ? 1 - Math.Pow(1 - t, 3) : t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }
    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (IsDisposed) return;
        IsDisposed = true;
        if (_active is { } run)
        {
            Volatile.Write(ref run.Stopped, 1); run.Subscription?.Dispose(); _active = null;
            run.Completion.TrySetCanceled();
        }
    }
    private sealed class Run(double from, double to, double seconds, Func<FenceAnimationFrame, Task> present)
    {
        public readonly double From = from, To = to, Seconds = seconds;
        public readonly Func<FenceAnimationFrame, Task> Present = present;
        public readonly long Started = Stopwatch.GetTimestamp();
        public long LastFrame = Stopwatch.GetTimestamp(), LatestPulse;
        public int Pending, Coalesced, Stopped;
        public bool Executing;
        public Exception? ClockFailure;
        public readonly List<double> Intervals = new(4096);
        public Action FrameAction = null!;
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? Subscription;
    }
}

public readonly record struct FenceAnimationFrame(double Progress, double EasedProgress, double Height);

public readonly record struct AnimationFrameMetrics(int Frames, double Fps, double AverageIntervalMs,
    double MaximumIntervalMs, int CoalescedFrames, int EstimatedMissedCadenceFrames = 0);
