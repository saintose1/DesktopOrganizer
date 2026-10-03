using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Core.Desktop;

/// <summary>Real items share the Fence trajectory; incomplete glyphs stay parked without clipping Explorer's HWND.</summary>
public sealed class FenceIconAnimationPlan
{
    private readonly DesktopIconTarget[] _start, _end, _parking;
    private readonly DesktopBounds[] _glyphOffsets;
    private readonly bool _collapsing;
    public IReadOnlyList<DesktopIconTarget> FinalTargets { get; }
    public int Count => _start.Length;
    public FenceIconAnimationPlan(Fence target, IReadOnlyList<DesktopIcon> snapshot, IReadOnlyList<DesktopIconTarget> finalTargets,
        IReadOnlyList<DesktopArea> monitors, bool startIsCollapsed, IReadOnlyList<DesktopIconTarget> parkingTargets, double headerHeight)
    {
        if (!double.IsFinite(headerHeight) || headerHeight < 0) throw new ArgumentOutOfRangeException(nameof(headerHeight));
        _collapsing = target.IsCollapsed;
        var matched = FenceIconLayout.Match(snapshot);
        var parking = parkingTargets.ToDictionary(i => i.StableId, FenceIconLayout.IdentityComparer);
        var icons = target.Icons.Where(i => matched.ContainsKey(i.StableId)).ToArray();
        _start = new DesktopIconTarget[icons.Length]; _end = new DesktopIconTarget[icons.Length];
        _parking = new DesktopIconTarget[icons.Length]; _glyphOffsets = new DesktopBounds[icons.Length];
        FinalTargets = finalTargets.ToArray();
        var slide = icons.Length == 0 ? 0 : Math.Clamp(icons.Min(i =>
            IconPlacement.Normal(target, i).Y + matched[i.StableId].Bounds.Y - matched[i.StableId].Position.Y - target.Y - headerHeight),
            0, icons.Max(i => matched[i.StableId].Bounds.Height));
        for (var i = 0; i < icons.Length; i++)
        {
            var item = matched[icons[i].StableId]; var normal = IconPlacement.Normal(target, icons[i]);
            if (!parking.TryGetValue(item.StableId, out _parking[i]))
                throw new ArgumentException("Each animated item requires a parking position.", nameof(parkingTargets));
            // Keep row spacing and the entire glyph below the interactive header.
            var localHidden = new DesktopPoint(normal.X, normal.Y - slide);
            var onScreen = monitors.Any(area => new DesktopBounds(area.X, area.Y, area.Width, area.Height).Contains(item.Position));
            _start[i] = new(item.StableId, startIsCollapsed || !onScreen ? localHidden : item.Position);
            _end[i] = new(item.StableId, target.IsCollapsed ? localHidden : normal);
            _glyphOffsets[i] = item.Bounds with { X = item.Bounds.X - item.Position.X, Y = item.Bounds.Y - item.Position.Y };
        }
    }
    public int Fill(double easedProgress, Span<DesktopIconTarget> targets) => Fill(easedProgress, easedProgress >= 1, targets);
    public int Fill(double easedProgress, bool finalFrame, Span<DesktopIconTarget> targets, DesktopBounds? content = null)
    {
        if (!double.IsFinite(easedProgress)) throw new ArgumentException("Invalid animation progress.", nameof(easedProgress));
        var progress = Math.Clamp(easedProgress, 0, 1);
        // Easing can round to 1 before elapsed time reaches the final frame.
        // Only the shared clock's final frame may include other groups' reindexed parking slots.
        var count = finalFrame ? FinalTargets.Count : Count;
        if (targets.Length < count) throw new ArgumentException("Animation buffer is too small.", nameof(targets));
        for (var i = 0; i < count; i++)
        {
            var position = finalFrame ? FinalTargets[i].Position : new DesktopPoint(
                _start[i].Position.X + (_end[i].Position.X - _start[i].Position.X) * progress,
                _start[i].Position.Y + (_end[i].Position.Y - _start[i].Position.Y) * progress);
            position = new(Math.Round(position.X), Math.Round(position.Y));
            if (!finalFrame && content is { } viewport)
            {
                var glyph = _glyphOffsets[i];
                var left = position.X + glyph.X; var top = position.Y + glyph.Y;
                if (left < viewport.X || top < viewport.Y || left + glyph.Width > viewport.X + viewport.Width ||
                    top + glyph.Height > viewport.Y + viewport.Height) position = _parking[i].Position;
            }
            targets[i] = new(finalFrame ? FinalTargets[i].StableId : _start[i].StableId, position);
        }
        return count;
    }
    public IReadOnlyList<DesktopIconTarget> InitialTargets => _start;
    public bool CanFinishParking(ReadOnlySpan<DesktopIconTarget> targets)
    {
        if (!_collapsing || targets.Length != Count) return false;
        for (var i = 0; i < Count; i++)
            if (!FenceIconLayout.IdentityComparer.Equals(targets[i].StableId, _parking[i].StableId) ||
                targets[i].Position != _parking[i].Position) return false;
        return true;
    }
}

public interface IDesktopIconAnimationPositioner
{
    Task<IDesktopIconAnimationSession> BeginAnimationAsync(IReadOnlyList<DesktopIconTarget> initialTargets,
        IReadOnlyList<DesktopIconTarget> finalTargets, CancellationToken cancellationToken = default);
}

public interface IDesktopIconAnimationSession : IAsyncDisposable
{
    Task PositionAsync(IReadOnlyList<DesktopIconTarget> targets, CancellationToken cancellationToken = default);
    Task CompleteFrameAsync(CancellationToken cancellationToken = default);
}
