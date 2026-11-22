using DesktopOrganizer.Core.Localization;
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
    public bool IsSearch { get; }
    public ImmutableArray<DesktopIconAssignment> Icons { get; private init; } = [];
    public FenceAppearance Appearance { get; private init; }
    // File-name rules for new desktop items (see FenceAutoSort); empty for groups without rules.
    public ImmutableArray<string> AutoSort { get; private init; } = [];

    public Fence(Guid id, string name, double x, double y, double width, double height, bool isCollapsed = false,
        IEnumerable<DesktopIconAssignment>? icons = null, FenceAppearance? appearance = null, IEnumerable<string>? autoSort = null,
        bool isSearch = false)
    {
        if (id == Guid.Empty)
            throw new ArgumentException(Texts.T("Идентификатор группы не может быть пустым."), nameof(id));
        var trimmedName = name?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length > MaxNameLength || trimmedName.Any(char.IsControl))
            throw new ArgumentException(Texts.T("Название должно содержать от 1 до 100 символов без управляющих символов."), nameof(name));
        ValidateBounds(x, y, width, height);
        Id = id;
        Name = trimmedName;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        IsSearch = isSearch;
        IsCollapsed = isCollapsed || isSearch;
        Appearance = appearance ?? FenceAppearance.Default;
        if (icons is not null) Icons = ValidateIcons(icons);
        if (autoSort is not null) AutoSort = ValidateAutoSort(autoSort);
        if (IsSearch && (!Icons.IsEmpty || !AutoSort.IsEmpty)) throw new ArgumentException("A search Fence cannot contain items or rules.");
    }

    public Fence WithBounds(double x, double y, double width, double height)
    {
        ValidateBounds(x, y, width, height);
        return this with { X = x, Y = y, Width = width, Height = height };
    }
    public Fence ToggleCollapsed() => IsSearch ? this : this with { IsCollapsed = !IsCollapsed };
    public Fence WithIcons(IEnumerable<DesktopIconAssignment> icons)
    {
        var assignments = ValidateIcons(icons);
        if (IsSearch && !assignments.IsEmpty) throw new ArgumentException("A search Fence cannot contain items.", nameof(icons));
        return this with { Icons = assignments };
    }
    public Fence WithPresentation(string name, FenceAppearance appearance) => new(Id, name, X, Y, Width, Height, IsCollapsed, Icons,
        appearance ?? throw new ArgumentNullException(nameof(appearance)), AutoSort, IsSearch);
    public Fence WithAutoSort(IEnumerable<string> patterns)
    {
        var rules = ValidateAutoSort(patterns);
        if (IsSearch && !rules.IsEmpty) throw new ArgumentException("A search Fence cannot contain rules.", nameof(patterns));
        return this with { AutoSort = rules };
    }
    private static ImmutableArray<string> ValidateAutoSort(IEnumerable<string> patterns)
    {
        var result = patterns.ToImmutableArray();
        if (result.Length > Desktop.FenceAutoSort.MaxPatterns) throw new ArgumentException(Texts.T("Слишком много правил авто-сортировки."));
        foreach (var pattern in result) Desktop.FenceAutoSort.Validate(pattern);
        return result;
    }
    private static void ValidateBounds(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException(Texts.T("Координаты должны быть конечными числами."));
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentException(Texts.T("Ширина и высота должны быть конечными числами больше нуля."));
    }
    private static ImmutableArray<DesktopIconAssignment> ValidateIcons(IEnumerable<DesktopIconAssignment> icons)
    {
        var result = icons.ToImmutableArray();
        if (result.Any(i => i is null) || result.Select(i => i.StableId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length)
            throw new ArgumentException(Texts.T("Назначения иконок должны иметь уникальные идентификаторы."));
        return result;
    }
}
