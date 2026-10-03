using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.App.Desktop;

/// <summary>One absolute virtual-screen-pixel projection. No incremental DIP/pixel conversions.</summary>
public sealed class FenceHeightAnimationGeometry(Fence target, DesktopArea area)
{
    private readonly double _left = Math.Max(target.X, area.X), _top = Math.Max(target.Y, area.Y);
    private readonly double _right = Math.Min(target.X + target.Width, (double)area.X + area.Width);
    public FenceSurfaceLayout? At(double height)
    {
        if (!double.IsFinite(height) || height <= 0) throw new ArgumentException("Invalid presentation height.", nameof(height));
        var bottom = Math.Min(target.Y + height, (double)area.Y + area.Height);
        if (_right <= _left || bottom <= _top) return null;
        var x = (int)Math.Floor(_left); var y = (int)Math.Floor(_top);
        return new(x, y, (int)Math.Ceiling(_right) - x, (int)Math.Ceiling(bottom) - y);
    }
}
