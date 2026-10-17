using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Core.Desktop;

/// <summary>Physical screen coordinates; parking never changes persisted Fence-relative offsets.</summary>
public static class IconPlacement
{
    public static DesktopPoint Parking(IReadOnlyList<DesktopArea> monitors, int slot, IconGridMetrics metrics)
    {
        if (monitors.Count == 0 || slot < 0) throw new ArgumentException("A monitor and nonnegative parking slot are required.");
        var left = monitors.Min(m => m.X);
        var top = monitors.Min(m => m.Y);
        return new(Math.Max(int.MinValue + 65536.0, left - 4096.0 - slot * Math.Max(128, metrics.StepX)), top - 4096.0);
    }

    public static DesktopPoint Normal(Fence fence, DesktopIconAssignment assignment) => new(fence.X + assignment.OffsetX, fence.Y + assignment.OffsetY);

    public static IReadOnlyList<DesktopIconTarget> Targets(IReadOnlyList<Fence> fences, IReadOnlyList<DesktopArea> monitors, IconGridMetrics metrics)
    {
        var result = new List<DesktopIconTarget>(); var slot = 0;
        foreach (var fence in fences)
        foreach (var icon in fence.Icons)
            result.Add(new(icon.StableId, fence.IsCollapsed ? Parking(monitors, slot++, metrics) : Normal(fence, icon)));
        return result;
    }

    public static IReadOnlyList<DesktopIconTarget> Recovery(IReadOnlyList<Fence> fences) => fences.Where(f => f.IsCollapsed)
        .SelectMany(f => f.Icons.Select(i => new DesktopIconTarget(i.StableId, Normal(f, i)))).ToArray();

    public static IReadOnlyList<DesktopIconTarget> ChangedFenceTargets(IReadOnlyList<Fence> before, IReadOnlyList<Fence> after,
        IReadOnlyList<DesktopArea> monitors, IconGridMetrics metrics)
    {
        var affected = after.Where(f => !before.Any(old => old.Id == f.Id && old.X == f.X && old.Y == f.Y &&
            old.Width == f.Width && old.Height == f.Height && old.IsCollapsed == f.IsCollapsed && old.Icons.SequenceEqual(f.Icons)))
            .SelectMany(f => f.Icons).Select(i => i.StableId).ToHashSet(FenceIconLayout.IdentityComparer);
        return Targets(after, monitors, metrics).Where(t => affected.Contains(t.StableId)).ToArray();
    }
    public static IReadOnlyList<DesktopIconTarget> ReturnPositions(IReadOnlyList<Fence> fences) => fences
        .SelectMany(f => f.Icons.Select(i => new DesktopIconTarget(i.StableId, Normal(f, i)))).ToArray();

    /// <summary>Explorer can bring offscreen items back into its grid after OLE drop; preserve only untouched collapsed parking.</summary>
    public static IReadOnlyList<DesktopIconTarget> DisplacedParking(IReadOnlyList<Fence> fences, IReadOnlyList<DesktopIcon> before,
        DesktopIconSnapshot after, IReadOnlyList<string> draggedIds, IReadOnlyList<DesktopArea> monitors)
    {
        if (!after.Connected || !after.CanPosition || after.Error is not null || monitors.Count == 0) return [];
        var previous = FenceIconLayout.Match(before); var current = FenceIconLayout.Match(after.Icons);
        var dragged = draggedIds.ToHashSet(FenceIconLayout.IdentityComparer);
        bool Visible(DesktopIcon icon) => monitors.Any(m => icon.Bounds.X < m.X + m.Width && icon.Bounds.X + icon.Bounds.Width > m.X &&
            icon.Bounds.Y < m.Y + m.Height && icon.Bounds.Y + icon.Bounds.Height > m.Y);
        return fences.Where(f => f.IsCollapsed).SelectMany(f => f.Icons)
            .Where(i => !dragged.Contains(i.StableId) && previous.TryGetValue(i.StableId, out var old) && !Visible(old) &&
                current.TryGetValue(i.StableId, out var item) && Visible(item))
            .Select(i => new DesktopIconTarget(i.StableId, previous[i.StableId].Position)).ToArray();
    }

    public static bool AtTargets(DesktopIconSnapshot snapshot, IReadOnlyList<DesktopIconTarget> targets) => snapshot.Connected && snapshot.CanPosition && snapshot.Error is null &&
        targets.All(t => snapshot.Icons.Any(i => FenceIconLayout.IdentityComparer.Equals(i.StableId, t.StableId) &&
            Math.Abs(i.Position.X - t.Position.X) + Math.Abs(i.Position.Y - t.Position.Y) <= 2));
}
