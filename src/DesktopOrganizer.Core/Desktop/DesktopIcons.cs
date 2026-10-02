namespace DesktopOrganizer.Core.Desktop;

public readonly record struct DesktopPoint(double X, double Y);
public readonly record struct DesktopBounds(double X, double Y, double Width, double Height)
{
    public bool Contains(DesktopPoint p) => p.X >= X && p.Y >= Y && p.X < X + Width && p.Y < Y + Height;
}
public readonly record struct DesktopCoordinates(DesktopPoint ClientOrigin, DesktopPoint ViewOrigin)
{
    public DesktopPoint ToScreen(DesktopPoint view) => new(ClientOrigin.X + view.X - ViewOrigin.X, ClientOrigin.Y + view.Y - ViewOrigin.Y);
    public DesktopPoint ToView(DesktopPoint screen) => new(screen.X - ClientOrigin.X + ViewOrigin.X, screen.Y - ClientOrigin.Y + ViewOrigin.Y);
}
public sealed record DesktopIcon(int Index, string StableId, string DisplayName, DesktopPoint ViewPosition,
    DesktopPoint Position, DesktopBounds Bounds);
public readonly record struct IconGridMetrics(double StepX, double StepY, double ItemWidth, double ItemHeight, double BoundsOffsetX = 0, double BoundsOffsetY = 0)
{
    public static IconGridMetrics Default => new(100, 110, 90, 100);
    public IconGridMetrics Validated() => new(
        Math.Max(1, double.IsFinite(StepX) ? StepX : 100), Math.Max(1, double.IsFinite(StepY) ? StepY : 110),
        Math.Max(1, double.IsFinite(ItemWidth) ? ItemWidth : 90), Math.Max(1, double.IsFinite(ItemHeight) ? ItemHeight : 100),
        double.IsFinite(BoundsOffsetX) ? BoundsOffsetX : 0, double.IsFinite(BoundsOffsetY) ? BoundsOffsetY : 0);
}
public sealed record DesktopIconSnapshot(bool Connected, bool CanPosition, IReadOnlyList<DesktopIcon> Icons,
    IconGridMetrics Metrics, string? Error = null, string? Notice = null);
public readonly record struct DesktopIconTarget(string StableId, DesktopPoint Position);
public interface IDesktopIconService
{
    Task<DesktopIconSnapshot> ReadAsync(CancellationToken cancellationToken = default);
    Task PositionAsync(IReadOnlyList<DesktopIconTarget> targets, CancellationToken cancellationToken = default);
}
