using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Core.Desktop;

[Flags]
public enum FenceHit { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8, Header = 16, Collapse = 32 }
public enum InteractionPhase { Started, Preview, Completed, Cancelled }
public sealed class FenceInteractionEventArgs(Fence fence, InteractionPhase phase) : EventArgs
{
    public Fence Fence { get; } = fence;
    public InteractionPhase Phase { get; } = phase;
    public FenceHit Hit { get; init; }
}

/// <summary>Pure geometry in the same screen coordinate system as the desktop host.</summary>
public static class FenceInteractionGeometry
{
    public const double HeaderHeight = 36, Border = 6, ButtonWidth = 32, MinimumWidth = 180, MinimumHeight = 90;
    public static double VisibleHeight(Fence fence, double scale) => fence.IsCollapsed ? HeaderHeight * scale : fence.Height;

    public static FenceHit HitTest(Fence fence, double x, double y, double scale)
    {
        var height = VisibleHeight(fence, scale);
        if (x < 0 || y < 0 || x >= fence.Width || y >= height) return FenceHit.None;
        var edge = Border * scale;
        var hit = x < edge ? FenceHit.Left : x >= fence.Width - edge ? FenceHit.Right : FenceHit.None;
        if (!fence.IsCollapsed) hit |= y < edge ? FenceHit.Top : y >= height - edge ? FenceHit.Bottom : FenceHit.None;
        if (hit != FenceHit.None) return hit;
        if (y >= HeaderHeight * scale) return FenceHit.None;
        return x >= fence.Width - edge - ButtonWidth * scale && y >= edge && y < HeaderHeight * scale - edge ? FenceHit.Collapse : FenceHit.Header;
    }

    public static Fence MoveOrResize(Fence original, FenceHit hit, double dx, double dy, double scale, IReadOnlyList<DesktopArea> monitors)
    {
        double x = original.X, y = original.Y, width = original.Width, height = original.Height;
        if (hit == FenceHit.Header) { x += dx; y += dy; }
        else
        {
            if (hit.HasFlag(FenceHit.Left)) { width = Math.Max(MinimumWidth * scale, original.Width - dx); x += original.Width - width; }
            if (hit.HasFlag(FenceHit.Right)) width = Math.Max(MinimumWidth * scale, original.Width + dx);
            if (!original.IsCollapsed)
            {
                if (hit.HasFlag(FenceHit.Top)) { height = Math.Max(MinimumHeight * scale, original.Height - dy); y += original.Height - height; }
                if (hit.HasFlag(FenceHit.Bottom)) height = Math.Max(MinimumHeight * scale, original.Height + dy);
            }
        }
        return KeepHeaderAccessible(original.WithBounds(x, y, width, height), scale, monitors);
    }

    // Clamp to the union of actual monitor work areas, not its bounding rectangle (which can contain gaps).
    public static Fence KeepHeaderAccessible(Fence fence, double scale, IReadOnlyList<DesktopArea> monitors)
    {
        var grip = Math.Min(fence.Width, 100 * scale);
        var header = Math.Min(VisibleHeight(fence, scale), HeaderHeight * scale);
        double best = double.PositiveInfinity, x = fence.X, y = fence.Y;
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            if (m.Width <= 0 || m.Height <= 0) continue;
            var px = Math.Clamp(fence.X, m.X - fence.Width + Math.Min(grip, m.Width), m.X + m.Width - Math.Min(grip, m.Width));
            var py = Math.Clamp(fence.Y, m.Y, m.Y + m.Height - Math.Min(header, m.Height));
            var dx = px - fence.X; var dy = py - fence.Y; var distance = dx * dx + dy * dy;
            if (distance < best) { best = distance; x = px; y = py; }
        }
        return x == fence.X && y == fence.Y ? fence : fence.WithBounds(x, y, fence.Width, fence.Height);
    }
}
