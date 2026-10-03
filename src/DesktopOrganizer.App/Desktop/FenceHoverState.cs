namespace DesktopOrganizer.App.Desktop;

/// <summary>A temporary presentation lease. It never owns or changes persisted collapse state.</summary>
public sealed class FenceHoverState
{
    public static readonly TimeSpan ExpandDelay = TimeSpan.Zero;
    public static readonly TimeSpan CollapseDelay = TimeSpan.Zero;
    public bool Expanded { get; private set; }
    public TimeSpan? DueAt { get; private set; }
    private bool _suppressed, _target;

    public void Observe(bool enabled, bool manuallyCollapsed, bool inside, bool onHandle, bool busy, TimeSpan now)
    {
        if (!inside) _suppressed = false;
        var target = enabled && manuallyCollapsed && !_suppressed && (Expanded ? inside : inside && onHandle);
        if (busy || target == Expanded) { DueAt = null; return; }
        if (DueAt is null || target != _target)
        {
            _target = target;
            DueAt = now + (target ? ExpandDelay : CollapseDelay);
        }
    }
    public bool? Due(TimeSpan now) => DueAt is { } at && now >= at ? _target : null;
    public void Presented(bool expanded) { Expanded = expanded; DueAt = null; }
    public void ManualAction() { _suppressed = true; DueAt = null; }
}
