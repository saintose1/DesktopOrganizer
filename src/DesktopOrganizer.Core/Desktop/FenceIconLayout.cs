using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Core.Desktop;

public static class FenceIconLayout
{
    public static StringComparer IdentityComparer => StringComparer.OrdinalIgnoreCase;
    public static DesktopBounds ContentBounds(Fence fence, double scale) => new(fence.X + 12 * scale,
        fence.Y + FenceInteractionGeometry.HeaderHeight * scale + 8 * scale,
        Math.Max(0, fence.Width - 24 * scale), Math.Max(0, fence.Height - (FenceInteractionGeometry.HeaderHeight + 20) * scale));
    public static double MinimumWidth(IconGridMetrics metrics, double scale) => Math.Max(FenceInteractionGeometry.MinimumWidth * scale, metrics.ItemWidth + 24 * scale);
    public static double RequiredHeight(double width, int count, IconGridMetrics metrics, double scale)
    {
        if (count == 0) return FenceInteractionGeometry.MinimumHeight * scale;
        var columns = Columns(width, metrics, scale);
        var rows = (count + columns - 1) / columns;
        return (FenceInteractionGeometry.HeaderHeight + 20) * scale + metrics.ItemHeight + (rows - 1) * Math.Max(metrics.StepY, metrics.ItemHeight);
    }
    private static int Columns(double width, IconGridMetrics metrics, double scale) =>
        Math.Max(1, (int)Math.Min(100000, Math.Floor(Math.Max(0, width - 24 * scale - metrics.ItemWidth) / Math.Max(metrics.StepX, metrics.ItemWidth)) + 1));
    public static DesktopPoint GridOffset(int index, double width, IconGridMetrics metrics, double scale)
    {
        var columns = Columns(width, metrics, scale);
        return new(12 * scale + index % columns * Math.Max(metrics.StepX, metrics.ItemWidth) - metrics.BoundsOffsetX,
            (FenceInteractionGeometry.HeaderHeight + 8) * scale + index / columns * Math.Max(metrics.StepY, metrics.ItemHeight) - metrics.BoundsOffsetY);
    }

    public static Fence ConstrainResize(Fence fence, Fence original, FenceHit hit, IconGridMetrics metrics, double scale)
    {
        if (fence.Icons.IsEmpty || fence.IsCollapsed || hit == FenceHit.Header) return fence;
        var width = Math.Max(fence.Width, MinimumWidth(metrics, scale));
        var height = Math.Max(fence.Height, RequiredHeight(width, fence.Icons.Length, metrics, scale));
        var x = hit.HasFlag(FenceHit.Left) ? original.X + original.Width - width : fence.X;
        var y = hit.HasFlag(FenceHit.Top) ? original.Y + original.Height - height : fence.Y;
        return fence.WithBounds(x, y, width, height);
    }

    public static Fence Arrange(Fence fence, IconGridMetrics metrics, double scale)
    {
        if (fence.Icons.IsEmpty) return fence;
        var width = Math.Max(fence.Width, MinimumWidth(metrics, scale));
        var height = Math.Max(fence.Height, RequiredHeight(width, fence.Icons.Length, metrics, scale));
        var columns = Columns(width, metrics, scale);
        var assignments = new DesktopIconAssignment[fence.Icons.Length];
        for (var i = 0; i < assignments.Length; i++)
        {
            var icon = fence.Icons[i];
            assignments[i] = new(icon.StableId, icon.DisplayName,
                12 * scale + i % columns * Math.Max(metrics.StepX, metrics.ItemWidth) - metrics.BoundsOffsetX,
                (FenceInteractionGeometry.HeaderHeight + 8) * scale + i / columns * Math.Max(metrics.StepY, metrics.ItemHeight) - metrics.BoundsOffsetY);
        }
        return fence.WithBounds(fence.X, fence.Y, width, height).WithIcons(assignments);
    }

    public static IReadOnlyList<Fence> ApplyMovedIcons(IReadOnlyList<Fence> fences, IReadOnlyList<DesktopIcon> moved,
        IconGridMetrics metrics, Func<Fence, double> scaleFor)
    {
        var groups = fences.ToArray();
        foreach (var icon in moved)
        {
            var center = new DesktopPoint(icon.Bounds.X + icon.Bounds.Width / 2, icon.Bounds.Y + icon.Bounds.Height / 2);
            var target = -1;
            // Latest Fence wins when surfaces overlap, matching their normal stacking order.
            for (var i = 0; i < groups.Length; i++)
                if (!groups[i].IsCollapsed && ContentBounds(groups[i], scaleFor(groups[i])).Contains(center)) target = i;
            for (var i = 0; i < groups.Length; i++)
            {
                var had = groups[i].Icons.Any(a => IdentityComparer.Equals(a.StableId, icon.StableId));
                if (!had && i != target) continue;
                if (had && i == target) continue;
                var remaining = groups[i].Icons.Where(a => !IdentityComparer.Equals(a.StableId, icon.StableId));
                groups[i] = groups[i].WithIcons(i == target ? remaining.Append(new(icon.StableId, icon.DisplayName, 0, 0)) : remaining);
                groups[i] = Arrange(groups[i], metrics, scaleFor(groups[i]));
            }
            if (target >= 0) groups[target] = Arrange(groups[target], metrics, scaleFor(groups[target]));
        }
        return groups;
    }
    public static Dictionary<string, DesktopIcon> Match(IReadOnlyList<DesktopIcon> icons)
    {
        var result = new Dictionary<string, DesktopIcon>(IdentityComparer);
        var ambiguous = new HashSet<string>(IdentityComparer);
        foreach (var icon in icons)
            if (!result.TryAdd(icon.StableId, icon)) ambiguous.Add(icon.StableId);
        foreach (var key in ambiguous) result.Remove(key);
        return result;
    }
}
