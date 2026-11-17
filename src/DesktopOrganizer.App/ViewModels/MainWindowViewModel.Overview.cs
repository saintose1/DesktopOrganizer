using System.IO;
using System.Collections.ObjectModel;
using DesktopOrganizer.Core.Localization;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.App.ViewModels;

public enum AppPage { Overview, Fences, Rules, Settings }

/// <summary>One tile of a group card on the Overview page. The picture is a decorative category icon, not the Explorer icon.</summary>
public sealed record OverviewItem(string Name, string IconUri);

public sealed record OverviewGroup(Guid Id, string Name, string CountText, IReadOnlyList<OverviewItem> Items, string MoreText,
    bool HasMore, bool IsEmpty, string StateText);

/// <summary>Maps a desktop item to one of the mossy category icons in Assets/Forest.</summary>
public static class ForestIcons
{
    private const string Folder = "pack://application:,,,/DesktopOrganizer.App;component/Assets/Forest/icon-";
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".exe"] = "code", [".msi"] = "code", [".bat"] = "terminal", [".cmd"] = "terminal", [".ps1"] = "terminal", [".com"] = "code",
        [".lnk"] = "shortcut", [".url"] = "shortcut", [".html"] = "globe", [".htm"] = "globe",
        [".pdf"] = "pdf", [".doc"] = "document", [".docx"] = "document", [".txt"] = "notes", [".rtf"] = "document", [".odt"] = "document", [".md"] = "notes",
        [".xls"] = "sheet", [".xlsx"] = "sheet", [".csv"] = "sheet", [".ppt"] = "presentation", [".pptx"] = "presentation",
        [".png"] = "picture", [".jpg"] = "picture", [".jpeg"] = "picture", [".gif"] = "picture", [".bmp"] = "picture", [".webp"] = "picture", [".svg"] = "picture",
        [".mp4"] = "video", [".mkv"] = "video", [".avi"] = "video", [".mov"] = "video", [".webm"] = "video",
        [".mp3"] = "music", [".wav"] = "music", [".flac"] = "music", [".ogg"] = "music",
        [".zip"] = "archive", [".rar"] = "archive", [".7z"] = "archive", [".tar"] = "archive", [".gz"] = "archive",
        [".iso"] = "drive", [".img"] = "drive", [".eml"] = "mail", [".msg"] = "mail", [".ics"] = "calendar",
        [".psd"] = "palette", [".ico"] = "palette", [".json"] = "code", [".xml"] = "code", [".cs"] = "code", [".js"] = "code", [".py"] = "code",
    };

    public static string For(string identity)
    {
        var name = identity.StartsWith("::", StringComparison.Ordinal)
            ? identity.Contains("645FF040", StringComparison.OrdinalIgnoreCase) ? "bin" : "desktop"
            : Path.GetExtension(identity.TrimEnd('\\', '/')) is { Length: > 0 } extension
                ? ByExtension.GetValueOrDefault(extension, "document")
                : "folder";
        return Folder + name + ".png";
    }
}

public sealed partial class MainWindowViewModel
{
    private const int OverviewItemsPerGroup = 6;
    public ObservableCollection<OverviewGroup> OverviewGroups { get; } = [];
    private string _statsText = "";
    public string StatsText { get => _statsText; private set => SetProperty(ref _statsText, value); }
    public bool HasNoGroups => _fences.Count == 0;
    private string _searchText = "";
    /// <summary>Filters the Overview cards by group name or item name.</summary>
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value ?? "")) RefreshOverview(); } }
    public bool NothingFound => _fences.Count > 0 && OverviewGroups.Count == 0;

    /// <summary>Rebuilds the Overview cards and the statistics line from the current groups.</summary>
    private void RefreshOverview()
    {
        OverviewGroups.Clear();
        var filter = SearchText.Trim();
        foreach (var fence in _fences)
        {
            if (filter.Length > 0 && !fence.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) &&
                !fence.Icons.Any(icon => icon.DisplayName.Contains(filter, StringComparison.CurrentCultureIgnoreCase))) continue;
            var shown = fence.Icons.Take(OverviewItemsPerGroup)
                .Select(icon => new OverviewItem(icon.DisplayName, ForestIcons.For(icon.StableId))).ToArray();
            var more = fence.Icons.Length - shown.Length;
            OverviewGroups.Add(new OverviewGroup(fence.Id, fence.Name, IconsText(fence.Icons.Length), shown,
                "+" + more, more > 0, fence.Icons.Length == 0,
                fence.IsCollapsed ? Texts.T("Свёрнута") : Texts.T("Развёрнута")));
        }
        var items = _fences.Sum(f => f.Icons.Length);
        var rules = _fences.Sum(f => f.AutoSort.Length);
        StatsText = string.Join("   |   ", GroupsText(_fences.Count), ItemsText(items), RulesText(rules));
        OnPropertyChanged(nameof(HasNoGroups)); OnPropertyChanged(nameof(NothingFound));
    }

    private static int Category(int n)
    {
        if (Texts.Language == Texts.English) return n == 1 ? 0 : 2;
        var lastTwo = n % 100; var last = n % 10;
        if (lastTwo is >= 11 and <= 14) return 2;
        return last == 1 ? 0 : last is >= 2 and <= 4 ? 1 : 2;
    }
    private static string GroupsText(int n) => Category(n) switch
    {
        0 => Texts.F("{0} группа", n), 1 => Texts.F("{0} группы", n), _ => Texts.F("{0} групп", n),
    };
    private static string ItemsText(int n) => Category(n) switch
    {
        0 => Texts.F("{0} элемент", n), 1 => Texts.F("{0} элемента", n), _ => Texts.F("{0} элементов", n),
    };
    private static string RulesText(int n) => Category(n) switch
    {
        0 => Texts.F("{0} правило", n), 1 => Texts.F("{0} правила", n), _ => Texts.F("{0} правил", n),
    };
    private static string IconsText(int n) => Category(n) switch
    {
        0 => Texts.F("{0} значок", n), 1 => Texts.F("{0} значка", n), _ => Texts.F("{0} значков", n),
    };
    private static string MatchesText(int n) => Category(n) switch
    {
        0 => Texts.F("{0} совпадение", n), 1 => Texts.F("{0} совпадения", n), _ => Texts.F("{0} совпадений", n),
    };
}
