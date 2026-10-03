using System.Collections.Immutable;

namespace DesktopOrganizer.Core.Models;

/// <summary>Immutable, validated state of a group. Changes create a new instance.</summary>
public sealed record Fence
{
    public const int MaxNameLength = 100;
    public Guid Id { get; }
    public string Name { get; }
    public double X { get; private init; }
    public double Y { get; private init; }
    public double Width { get; private init; }
    public double Height { get; private init; }
    // Height is always the normal (expanded) height, including while collapsed.
    public bool IsCollapsed { get; private init; }
    public ImmutableArray<DesktopIconAssignment> Icons { get; private init; } = [];
    public FenceAppearance Appearance { get; private init; }

    public Fence(Guid id, string name, double x, double y, double width, double height, bool isCollapsed = false,
        IEnumerable<DesktopIconAssignment>? icons = null, FenceAppearance? appearance = null)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Идентификатор группы не может быть пустым.", nameof(id));
        var trimmedName = name?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length > MaxNameLength || trimmedName.Any(char.IsControl))
            throw new ArgumentException("Название должно содержать от 1 до 100 символов без управляющих символов.", nameof(name));
        ValidateBounds(x, y, width, height);
        Id = id;
        Name = trimmedName;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        IsCollapsed = isCollapsed;
        Appearance = appearance ?? FenceAppearance.Default;
        if (icons is not null) Icons = ValidateIcons(icons);
    }

    public Fence WithBounds(double x, double y, double width, double height)
    {
        ValidateBounds(x, y, width, height);
        return this with { X = x, Y = y, Width = width, Height = height };
    }
    public Fence ToggleCollapsed() => this with { IsCollapsed = !IsCollapsed };
    public Fence WithIcons(IEnumerable<DesktopIconAssignment> icons) => this with { Icons = ValidateIcons(icons) };
    public Fence WithPresentation(string name, FenceAppearance appearance) => new(Id, name, X, Y, Width, Height, IsCollapsed, Icons,
        appearance ?? throw new ArgumentNullException(nameof(appearance)));
    private static void ValidateBounds(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException("Координаты должны быть конечными числами.");
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentException("Ширина и высота должны быть конечными числами больше нуля.");
    }
    private static ImmutableArray<DesktopIconAssignment> ValidateIcons(IEnumerable<DesktopIconAssignment> icons)
    {
        var result = icons.ToImmutableArray();
        if (result.Any(i => i is null) || result.Select(i => i.StableId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length)
            throw new ArgumentException("Назначения иконок должны иметь уникальные идентификаторы.");
        return result;
    }
}
