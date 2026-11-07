using DesktopOrganizer.Core.Localization;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Core.Desktop;

/// <summary>
/// File-name rules per Fence ("*.png", "*.lnk", "Steam*"): a new, unassigned desktop item that matches joins the
/// first Fence with a matching rule. Existing items and assignments are never moved by a rule.
/// </summary>
public static class FenceAutoSort
{
    public const int MaxPatterns = 32, MaxPatternLength = 64;
    private static readonly char[] Separators = [';', ',', ' ', '\t', '\r', '\n'];

    /// <summary>"png; .jpg, *.gif" becomes "*.png", "*.jpg", "*.gif"; names with a wildcard are kept as typed.</summary>
    public static IReadOnlyList<string> Parse(string? text)
    {
        var result = new List<string>();
        foreach (var raw in (text ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var pattern = raw.Contains('*') || raw.Contains('?') ? raw : raw.StartsWith('.') ? "*" + raw : "*." + raw;
            Validate(pattern);
            if (!result.Contains(pattern, StringComparer.OrdinalIgnoreCase)) result.Add(pattern);
        }
        if (result.Count > MaxPatterns) throw new ArgumentException(Texts.F("Не более {0} правил авто-сортировки.", MaxPatterns));
        return result;
    }
    public static string Format(IEnumerable<string> patterns) => string.Join("; ", patterns);

    public static void Validate(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > MaxPatternLength || pattern.IndexOfAny(['\\', '/', ':', '"', '<', '>', '|']) >= 0 ||
            pattern.Any(char.IsControl))
            throw new ArgumentException(Texts.F("Неверное правило авто-сортировки «{0}». Пример: *.png; *.lnk; Steam*", pattern));
    }

    public static bool Matches(string identity, string pattern)
    {
        var name = Path.GetFileName(identity.TrimEnd('\\', '/'));
        return name.Length != 0 && Glob(name.AsSpan(), pattern.AsSpan());
    }

    public static Fence? TargetFor(IReadOnlyList<Fence> fences, string identity) =>
        fences.FirstOrDefault(f => f.AutoSort.Any(p => Matches(identity, p)));

    public static IReadOnlyList<Fence> AssignNew(IReadOnlyList<Fence> fences, IReadOnlyList<DesktopIcon> newcomers,
        IconGridMetrics metrics, Func<Fence, double> scaleFor)
    {
        var assigned = fences.SelectMany(f => f.Icons).Select(i => i.StableId).ToHashSet(FenceIconLayout.IdentityComparer);
        var additions = new Dictionary<Guid, List<DesktopIcon>>();
        foreach (var icon in newcomers)
        {
            if (assigned.Contains(icon.StableId) || TargetFor(fences, icon.StableId) is not { } target) continue;
            if (!additions.TryGetValue(target.Id, out var list)) additions[target.Id] = list = [];
            list.Add(icon); assigned.Add(icon.StableId);
        }
        if (additions.Count == 0) return fences;
        return fences.Select(fence => additions.TryGetValue(fence.Id, out var icons)
            ? FenceIconLayout.Arrange(fence.WithIcons(fence.Icons.Concat(icons.Select(i => new DesktopIconAssignment(i.StableId, i.DisplayName, 0, 0)))),
                metrics, scaleFor(fence))
            : fence).ToArray();
    }

    private static bool Glob(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t]))) { t++; p++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
