namespace DesktopOrganizer.Core.Desktop;

/// <summary>Separate clipped rectangles; a parked icon never expands damage to the entire virtual desktop.</summary>
public static class DesktopDamage
{
    public static IReadOnlyList<DesktopArea> Clip(IEnumerable<DesktopBounds> dirty, DesktopArea viewport)
    {
        var result = new List<DesktopArea>();
        foreach (var bounds in dirty)
        {
            var left = Math.Max(viewport.X, Math.Floor(bounds.X)); var top = Math.Max(viewport.Y, Math.Floor(bounds.Y));
            var right = Math.Min((double)viewport.X + viewport.Width, Math.Ceiling(bounds.X + bounds.Width));
            var bottom = Math.Min((double)viewport.Y + viewport.Height, Math.Ceiling(bounds.Y + bounds.Height));
            if (right > left && bottom > top) result.Add(new((int)left - viewport.X, (int)top - viewport.Y, (int)(right - left), (int)(bottom - top)));
        }
        return result;
    }
}
