using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Core.Desktop;

/// <summary>
/// Magnetic edges for a Fence being moved by its header: monitor work-area edges and the edges of other Fences
/// (side by side, aligned, or stacked). Pure geometry; X and Y snap independently to the nearest candidate.
/// </summary>
public static class FenceSnapping
{
    public const double Threshold = 12;

    public static Fence Snap(Fence moving, double visibleHeight, IReadOnlyList<DesktopBounds> others,
        IReadOnlyList<DesktopArea> monitors, double scale)
    {
        var threshold = Threshold * scale;
        var gap = 0.0;
        double left = moving.X, top = moving.Y, right = moving.X + moving.Width, bottom = moving.Y + visibleHeight;
        var xs = new List<double>(); var ys = new List<double>();
        foreach (var m in monitors)
        {
            xs.Add(m.X); xs.Add(m.X + m.Width - moving.Width);                 // left/right screen edge
            ys.Add(m.Y); ys.Add(m.Y + m.Height - visibleHeight);               // top/bottom screen edge
        }
        foreach (var o in others)
        {
            // Only Fences that overlap on the other axis (or nearly) are neighbours worth snapping to.
            var verticalNeighbour = top < o.Y + o.Height + threshold && bottom > o.Y - threshold;
            var horizontalNeighbour = left < o.X + o.Width + threshold && right > o.X - threshold;
            if (verticalNeighbour)
            {
                xs.Add(o.X + o.Width + gap);              // to the right of it
                xs.Add(o.X - moving.Width - gap);         // to the left of it
                xs.Add(o.X);                              // left edges aligned
                xs.Add(o.X + o.Width - moving.Width);     // right edges aligned
            }
            if (horizontalNeighbour)
            {
                ys.Add(o.Y + o.Height + gap);             // below it
                ys.Add(o.Y - visibleHeight - gap);        // above it
                ys.Add(o.Y);                              // tops aligned
            }
        }
        var x = Nearest(moving.X, xs, threshold);
        var y = Nearest(moving.Y, ys, threshold);
        return x == moving.X && y == moving.Y ? moving : moving.WithBounds(x, y, moving.Width, moving.Height);
    }

    private static double Nearest(double value, List<double> candidates, double threshold)
    {
        var best = value; var distance = threshold;
        foreach (var candidate in candidates)
        {
            var d = Math.Abs(candidate - value);
            if (d <= distance) { distance = d; best = candidate; }
        }
        return best;
    }
}
