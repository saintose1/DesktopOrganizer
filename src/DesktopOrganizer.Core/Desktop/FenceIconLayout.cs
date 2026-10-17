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

    /// <summary>Only confirmed missing identities are removed; native rename pairs preserve ownership and offsets.</summary>
    public static IReadOnlyList<Fence> ReconcileMembership(IReadOnlyList<Fence> fences, DesktopIconSnapshot snapshot,
        IReadOnlySet<string> missing, IReadOnlyDictionary<string, string> renames, Func<Fence, double> scaleFor)
    {
        if (!snapshot.Connected || snapshot.Error is not null) return fences;
        var present = Match(snapshot.Icons);
        var assigned = fences.SelectMany(f => f.Icons).Select(i => i.StableId).ToHashSet(IdentityComparer);
        return fences.Select(fence =>
        {
            var remaining = new List<DesktopIconAssignment>();
            foreach (var icon in fence.Icons)
            {
                var renamed = icon.StableId;
                var visited = new HashSet<string>(IdentityComparer);
                while (renames.TryGetValue(renamed, out var next) && visited.Add(renamed)) renamed = next;
                if (visited.Contains(renamed)) { remaining.Add(icon); continue; }
                if (!IdentityComparer.Equals(renamed, icon.StableId) && !present.ContainsKey(icon.StableId) &&
                    present.TryGetValue(renamed, out var item) && !assigned.Contains(renamed))
                {
                    remaining.Add(new(renamed, item.DisplayName, icon.OffsetX, icon.OffsetY));
                    assigned.Add(renamed);
                }
                else if (!missing.Contains(icon.StableId) || present.ContainsKey(icon.StableId)) remaining.Add(icon);
            }
            if (fence.Icons.SequenceEqual(remaining)) return fence;
            var changed = fence.WithIcons(remaining);
            return remaining.Count == fence.Icons.Length ? changed : Arrange(changed, snapshot.Metrics, scaleFor(fence));
        }).ToArray();
    }

    public static Fence? DropTarget(IReadOnlyList<Fence> fences, DesktopPoint point, Func<Fence, double> scaleFor) =>
        fences.LastOrDefault(f => !f.IsCollapsed && point.X >= f.X && point.X < f.X + f.Width &&
            point.Y >= f.Y + FenceInteractionGeometry.HeaderHeight * scaleFor(f) && point.Y < f.Y + f.Height);

    /// <summary>One target for the entire captured Shell selection, regardless of individual final item bounds.</summary>
    public static IReadOnlyList<Fence> ApplyGroupDrop(IReadOnlyList<Fence> fences, ExplorerDrop drop,
        IReadOnlyList<DesktopIcon> finalIcons, IconGridMetrics metrics, Func<Fence, double> scaleFor)
    {
        if (!drop.DesktopTarget) return fences;
        var matched = Match(finalIcons);
        var ids = drop.DraggedIds.Where(matched.ContainsKey).ToHashSet(IdentityComparer);
        if (ids.Count == 0 || !ids.Contains(drop.DraggedItemId)) return fences;
        var target = DropTarget(fences, drop.Point, scaleFor);
        return fences.Select(fence =>
        {
            var remaining = fence.Icons.Where(i => !ids.Contains(i.StableId)).ToList();
            if (fence.Id == target?.Id)
            {
                // Preserve existing order; append all newcomers once, then arrange only this affected Fence.
                remaining = fence.Icons.ToList();
                var existing = remaining.Select(i => i.StableId).ToHashSet(IdentityComparer);
                foreach (var id in drop.DraggedIds.Distinct(IdentityComparer))
                    if (matched.TryGetValue(id, out var icon) && existing.Add(id)) remaining.Add(new(id, icon.DisplayName, 0, 0));
            }
            return fence.Icons.SequenceEqual(remaining) && fence.Id != target?.Id ? fence : Arrange(fence.WithIcons(remaining), metrics, scaleFor(fence));
        }).ToArray();
    }

    public static IReadOnlyList<DesktopIconTarget> ChangedTargets(IReadOnlyList<DesktopIconTarget> targets, IReadOnlyList<DesktopIcon> icons)
    {
        var matched = Match(icons);
        return targets.Where(t => matched.TryGetValue(t.StableId, out var icon) &&
            Math.Abs(icon.Position.X - t.Position.X) + Math.Abs(icon.Position.Y - t.Position.Y) > 1).ToArray();
    }
}
