using DesktopOrganizer.App.ViewModels;
using DesktopOrganizer.Core.Localization;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Infrastructure.Settings;
using DesktopOrganizer.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopOrganizer.Tests;

/// <summary>Forest UI: pages, Overview statistics, Rules page, theme preference and the Forest preset.</summary>
/// <remarks>Asserts Russian text, so it never runs in parallel with LocalizationTests, which switches the language.</remarks>
[Collection("Interface language")]
public sealed class ForestUiTests
{
    private static MainWindowViewModel ViewModel(TestDirectory directory)
        => new(new JsonFenceStore(directory.DirectoryPath, NullLogger<JsonFenceStore>.Instance), NullLogger<MainWindowViewModel>.Instance);

    [Fact]
    public void PagesAreExclusiveAndStartOnOverview()
    {
        using var directory = new TestDirectory();
        using var vm = ViewModel(directory);
        Assert.True(vm.IsOverviewPage);
        foreach (var (command, check) in new (System.Windows.Input.ICommand, Func<bool>)[]
        {
            (vm.OpenFencesCommand, () => vm.IsFencesPage), (vm.OpenRulesCommand, () => vm.IsRulesPage),
            (vm.OpenSettingsCommand, () => vm.IsSettingsPage), (vm.OpenOverviewCommand, () => vm.IsOverviewPage),
        })
        {
            command.Execute(null);
            Assert.True(check());
            Assert.Equal(1, new[] { vm.IsOverviewPage, vm.IsFencesPage, vm.IsRulesPage, vm.IsSettingsPage }.Count(page => page));
        }
    }

    [Fact]
    public void OverviewCountsGroupsItemsAndRulesWithRussianPlurals()
    {
        using var directory = new TestDirectory();
        var store = new JsonFenceStore(directory.DirectoryPath, NullLogger<JsonFenceStore>.Instance);
        var first = new Fence(Guid.NewGuid(), "Игры", 0, 0, 300, 200)
            .WithIcons([new DesktopIconAssignment(@"C:\Desktop\Steam.lnk", "Steam", 0, 0), new DesktopIconAssignment(@"C:\Desktop\notes.txt", "notes", 0, 0)])
            .WithAutoSort(["*.lnk"]);
        store.Save([first, new Fence(Guid.NewGuid(), "Пусто", 400, 0, 300, 200)]);
        using var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance);
        Assert.Equal("2 группы   |   2 элемента   |   1 правило", vm.StatsText);
        Assert.Equal(2, vm.OverviewGroups.Count);
        Assert.Equal(2, vm.OverviewGroups[0].Items.Count);
        Assert.True(vm.OverviewGroups[1].IsEmpty);
        Assert.False(vm.OverviewGroups[0].HasMore);
        vm.SearchText = "steam";
        Assert.Equal("Игры", Assert.Single(vm.OverviewGroups).Name);
        vm.SearchText = "нет такого";
        Assert.Empty(vm.OverviewGroups);
        Assert.True(vm.NothingFound);
    }

    [Theory]
    [InlineData(1, "1 группа")]
    [InlineData(2, "2 группы")]
    [InlineData(5, "5 групп")]
    [InlineData(11, "11 групп")]
    [InlineData(21, "21 группа")]
    public void StatisticsUseTheCorrectRussianForm(int count, string expected)
    {
        using var directory = new TestDirectory();
        var store = new JsonFenceStore(directory.DirectoryPath, NullLogger<JsonFenceStore>.Instance);
        store.Save(Enumerable.Range(0, count).Select(i => new Fence(Guid.NewGuid(), "G" + i, i * 10, 0, 300, 200)).ToArray());
        using var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance);
        Assert.StartsWith(expected + "   |", vm.StatsText);
    }

    [Theory]
    [InlineData(@"C:\Desktop\Steam.lnk", "icon-shortcut.png")]
    [InlineData(@"C:\Desktop\Отчёт.PDF", "icon-pdf.png")]
    [InlineData(@"C:\Desktop\song.mp3", "icon-music.png")]
    [InlineData(@"C:\Desktop\Projects", "icon-folder.png")]
    [InlineData("::{645FF040-5081-101B-9F08-00AA002F954E}", "icon-bin.png")]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "icon-desktop.png")]
    [InlineData(@"C:\Desktop\data.unknownext", "icon-document.png")]
    public void ItemsGetADecorativeCategoryIcon(string identity, string file) => Assert.EndsWith(file, ForestIcons.For(identity));

    [Fact]
    public void EveryCategoryIconReferencedByTheMapExistsInTheAssets()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DesktopOrganizer.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var folder = Path.Combine(root.FullName, "src", "DesktopOrganizer.App", "Assets", "Forest");
        foreach (var identity in new[] { "a.exe", "a.bat", "a.lnk", "a.url", "a.pdf", "a.docx", "a.txt", "a.xlsx", "a.pptx", "a.png", "a.mp4", "a.mp3", "a.zip",
                     "a.iso", "a.eml", "a.ics", "a.psd", "a.json", "folder", "::{645FF040}", "::{x}", "a.unknownext" })
            Assert.True(File.Exists(Path.Combine(folder, ForestIcons.For(identity)[(ForestIcons.For(identity).LastIndexOf('/') + 1)..])), identity);
        foreach (var asset in new[] { "banner.jpg", "moss-frond.png" }) Assert.True(File.Exists(Path.Combine(folder, asset)), asset);
    }

    [Fact]
    public void RulesPageAddsAndRemovesMasksThroughTheGroupAndPersistsThem()
    {
        using var directory = new TestDirectory();
        var store = new JsonFenceStore(directory.DirectoryPath, NullLogger<JsonFenceStore>.Instance);
        using var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance);
        vm.CreateCommand.Execute(null);
        vm.OpenRulesCommand.Execute(null);
        Assert.True(vm.HasNoRules);
        Assert.Equal(vm.SelectedFence!.Id, vm.NewRuleFenceId);

        vm.NewRuleMask = "pdf; *.lnk";
        vm.AddRuleCommand.Execute(null);
        Assert.Equal(["*.pdf", "*.lnk"], vm.Rules.Select(r => r.Mask));
        Assert.Equal(["*.pdf", "*.lnk"], store.Load().Fences.Single().AutoSort);
        Assert.Equal("", vm.NewRuleMask);

        vm.NewRuleMask = "*.PDF"; // already present, any case
        vm.AddRuleCommand.Execute(null);
        Assert.Equal(2, vm.Rules.Count);

        vm.RemoveRuleCommand.Execute(vm.Rules[0]);
        Assert.Equal("*.lnk", Assert.Single(vm.Rules).Mask);
        Assert.Equal(["*.lnk"], store.Load().Fences.Single().AutoSort);
    }

    [Fact]
    public void RulesPageRejectsBadMasksWithoutChangingAnything()
    {
        using var directory = new TestDirectory();
        var store = new JsonFenceStore(directory.DirectoryPath, NullLogger<JsonFenceStore>.Instance);
        using var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance);
        vm.CreateCommand.Execute(null);
        vm.OpenRulesCommand.Execute(null);
        vm.NewRuleMask = "bad/mask";
        vm.AddRuleCommand.Execute(null);
        Assert.True(vm.HasError);
        Assert.Empty(vm.Rules);
        Assert.True(store.Load().Fences.Single().AutoSort.IsEmpty);
        vm.NewRuleMask = "";
        vm.AddRuleCommand.Execute(null);
        Assert.True(vm.HasError);
    }

    [Fact]
    public void ReadyMadeMaskFillsTheFieldAndIsAValidRule()
    {
        using var directory = new TestDirectory();
        var store = new JsonFenceStore(directory.DirectoryPath, NullLogger<JsonFenceStore>.Instance);
        using var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance);
        vm.CreateCommand.Execute(null);
        vm.OpenRulesCommand.Execute(null);
        vm.UseMaskCommand.Execute("*.exe; *.msi");
        Assert.Equal("*.exe; *.msi", vm.NewRuleMask);
        vm.AddRuleCommand.Execute(null);
        Assert.Equal(["*.exe", "*.msi"], store.Load().Fences.Single().AutoSort);
    }

    [Fact]
    public void RulePreviewIgnoresInvalidMasks()
    {
        using var directory = new TestDirectory();
        using var vm = ViewModel(directory);
        vm.OpenRulesCommand.Execute(null);
        vm.NewRuleMask = "a/b";
        Assert.Empty(vm.RulePreview);
        Assert.False(string.IsNullOrEmpty(vm.RulePreviewSummary));
    }

    [Fact]
    public void ForestPresetAppliesTheForestAppearanceInOneUpdate()
    {
        var updates = new List<FenceAppearance>();
        var editor = new FenceAppearanceViewModel((_, appearance) => updates.Add(appearance));
        editor.ForestPresetCommand.Execute(null);
        var applied = Assert.Single(updates);
        Assert.Equal("#E4E6D3", applied.TextColor);
        Assert.Equal("#14221A", applied.BodyColor);
        Assert.Equal("#86A35F", applied.BorderColor);
        Assert.Equal(85, applied.BodyOpacity);
        Assert.Equal(60, applied.BorderOpacity);
        Assert.True(applied.Bold);
        Assert.Contains(applied.FontFamily, new[] { "Noto Serif", "Georgia" });
        Assert.False(editor.HasError);
    }

    [Fact]
    public void ThemePreferenceIsStoredAndOldFilesFollowWindows()
    {
        using var directory = new TestDirectory();
        var store = new JsonAppPreferences(directory.DirectoryPath, NullLogger<JsonAppPreferences>.Instance);
        Assert.Null(store.Load().Theme);
        store.Save(new AppPreferences(true, 0.2, 0.1, "en", AppPreferences.LightTheme));
        var loaded = store.Load();
        Assert.Equal(AppPreferences.LightTheme, loaded.Theme);
        Assert.Equal("en", loaded.Language);
        Assert.True(loaded.AutoExpandOnHover);
        File.WriteAllText(Path.Combine(directory.DirectoryPath, "settings.json"), """{"AutoExpandOnHover":true,"ExpandAnimationSeconds":0.2,"CollapseAnimationSeconds":0.1}""");
        Assert.Null(store.Load().Theme);
        Assert.Null(new AppPreferences(theme: "purple").Theme);
    }
}
