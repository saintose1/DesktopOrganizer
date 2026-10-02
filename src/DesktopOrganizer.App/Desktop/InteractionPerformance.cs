using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.App.Desktop;

internal sealed class InteractionPerformance
{
    private long _started;
    private int _count;
    private double _total, _maximum;
    private double _iconTotal, _iconMaximum;
    private long _repaints, _presents;
    private string _kind = "Drag";
    [Conditional("DEBUG")]
    public void Begin(string kind, long repaints, long presents) { _kind = kind; _repaints = repaints; _presents = presents; _started = Stopwatch.GetTimestamp(); _count = 0; _total = _maximum = _iconTotal = _iconMaximum = 0; }
    [Conditional("DEBUG")]
    public void Record(long start, double iconTime = 0)
    {
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _count++; _total += elapsed; _maximum = Math.Max(_maximum, elapsed);
        _iconTotal += iconTime; _iconMaximum = Math.Max(_iconMaximum, iconTime);
    }
    [Conditional("DEBUG")]
    public void Report(ILogger logger, long repaints, long presents)
    {
        if (_count == 0) return;
        logger.LogInformation("{Kind} performance: Frames={Count}; Mean={Mean:F3}ms; Max={Maximum:F3}ms; IconMean={IconMean:F3}ms; IconMax={IconMax:F3}ms; Repaints={Repaints}; BitmapPresents={Presents}; Frames/sec={Rate:F1}",
            _kind, _count, _total / _count, _maximum, _iconTotal / _count, _iconMaximum, repaints - _repaints, presents - _presents,
            _count / Math.Max(0.001, Stopwatch.GetElapsedTime(_started).TotalSeconds));
    }
}
