using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.Infrastructure.Interop;

public sealed record ExplorerTracePoint(string Stage, double Milliseconds, int Count);

/// <summary>Bounded timestamps for one gesture, emitted once; never logs identities or mouse-move frames.</summary>
public sealed class ExplorerInteractionTrace(ILogger<ExplorerInteractionTrace> logger)
{
    private readonly object _gate = new();
    private readonly List<ExplorerTracePoint> _points = [];
    private long _started;
    private bool _active;
    public IReadOnlyList<ExplorerTracePoint> Snapshot { get { lock (_gate) return _points.ToArray(); } }
    public void Begin()
    {
        lock (_gate) { _points.Clear(); _started = Stopwatch.GetTimestamp(); _active = true; }
        Mark("Press");
    }
    public void Mark(string stage, int count = 0)
    {
        lock (_gate)
            if (_active && _points.Count < 128)
                _points.Add(new(stage, Stopwatch.GetElapsedTime(_started).TotalMilliseconds, count));
    }
    public void Complete(string outcome)
    {
        string timeline;
        lock (_gate)
        {
            if (!_active) return;
            _active = false;
            timeline = string.Join("; ", _points.Select(p => p.Stage + "@" + p.Milliseconds.ToString("F3", CultureInfo.InvariantCulture) + "ms" + (p.Count == 0 ? "" : "#" + p.Count)));
        }
        logger.LogInformation("Explorer gesture trace: Outcome={Outcome}; Timeline={Timeline}", outcome, timeline);
    }
}
