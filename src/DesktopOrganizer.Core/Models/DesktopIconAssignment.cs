namespace DesktopOrganizer.Core.Models;

public sealed record DesktopIconAssignment
{
    public string StableId { get; }
    public string DisplayName { get; }
    public double OffsetX { get; }
    public double OffsetY { get; }
    public DesktopIconAssignment(string stableId, string displayName, double offsetX, double offsetY)
    {
        if (string.IsNullOrWhiteSpace(stableId) || stableId.Length > 32768 || stableId.Any(char.IsControl))
            throw new ArgumentException("Недопустимый идентификатор desktop icon.", nameof(stableId));
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 2048)
            throw new ArgumentException("Недопустимое имя desktop icon.", nameof(displayName));
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY)) throw new ArgumentException("Icon offsets must be finite.");
        StableId = stableId; DisplayName = displayName; OffsetX = offsetX; OffsetY = offsetY;
    }
}
