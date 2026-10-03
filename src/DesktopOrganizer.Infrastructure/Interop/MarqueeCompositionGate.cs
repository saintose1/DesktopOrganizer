using DesktopOrganizer.Core.Desktop;

namespace DesktopOrganizer.Infrastructure.Interop;

/// <summary>Ordinary clicks never change Explorer composition; only classified background motion does.</summary>
internal sealed class MarqueeCompositionGate
{
    private DesktopPoint _origin;
    private double _thresholdX, _thresholdY;
    private bool _active, _background, _moved, _started;

    public void Begin(DesktopPoint origin, double thresholdX, double thresholdY)
    {
        _origin = origin; _thresholdX = thresholdX; _thresholdY = thresholdY;
        _active = true; _background = false; _moved = false; _started = false;
    }
    public bool Move(DesktopPoint point)
    {
        if (_active && (Math.Abs(point.X - _origin.X) > _thresholdX || Math.Abs(point.Y - _origin.Y) > _thresholdY)) _moved = true;
        return TryStart();
    }
    public bool Classify(bool background) { _background = background; return TryStart(); }
    public void End() => _active = false;
    private bool TryStart()
    {
        if (!_active || !_background || !_moved || _started) return false;
        _started = true; return true;
    }
}
