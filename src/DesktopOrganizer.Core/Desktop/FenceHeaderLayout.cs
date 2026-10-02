namespace DesktopOrganizer.Core.Desktop;

public enum FenceHeaderAction { ToggleCollapse }
public readonly record struct HeaderActionArea(FenceHeaderAction Action, double X, double Y, double Width, double Height)
{
    public bool Contains(double x, double y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>Shared rendering/input layout. Additional header actions can occupy new slots.</summary>
public sealed class FenceHeaderLayout
{
    public double Height { get; }
    public double TitleWidth { get; }
    public IReadOnlyList<HeaderActionArea> Actions { get; }

    public FenceHeaderLayout(double width, double scale, IReadOnlyList<FenceHeaderAction>? actions = null)
    {
        actions ??= [FenceHeaderAction.ToggleCollapse];
        Height = FenceInteractionGeometry.HeaderHeight * scale;
        var slot = FenceInteractionGeometry.ButtonWidth * scale;
        var border = FenceInteractionGeometry.Border * scale;
        Actions = actions.Select((action, index) => new HeaderActionArea(action,
            width - border - slot * (index + 1), border, slot, Height - 2 * border)).ToArray();
        TitleWidth = Math.Max(1, width - border - actions.Count * slot - 24 * scale);
    }
    public FenceHeaderAction? ActionAt(double x, double y)
    {
        foreach (var area in Actions)
            if (area.Contains(x, y)) return area.Action;
        return null;
    }
}
