using System.Net;
using System.Text.RegularExpressions;
using DesktopOrganizer.Core.Localization;

namespace DesktopOrganizer.Tests;

public sealed partial class LocalizationTests
{
    private static string Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DesktopOrganizer.sln"))) root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
    private static IEnumerable<string> Sources(string pattern) => Directory.EnumerateFiles(Path.Combine(Root(), "src"), pattern, SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void EveryTranslatedStringInCodeAndXamlHasAnEnglishEntry()
    {
        var keys = new List<(string File, string Key)>();
        foreach (var file in Sources("*.cs").Where(f => !f.EndsWith("Texts.cs", StringComparison.Ordinal)))
            foreach (Match match in CodeKey().Matches(File.ReadAllText(file)))
                keys.Add((file, Regex.Unescape(match.Groups[1].Value)));
        foreach (var file in Sources("*.xaml"))
            foreach (Match match in XamlKey().Matches(File.ReadAllText(file)))
                keys.Add((file, WebUtility.HtmlDecode(match.Groups[1].Value)));
        Assert.True(keys.Count > 100, $"Only {keys.Count} translated strings found; the scan is broken.");
        var missing = keys.Where(k => !Texts.HasTranslation(k.Key)).Select(k => $"{Path.GetFileName(k.File)}: {k.Key}").Distinct().ToArray();
        Assert.True(missing.Length == 0, "Missing English text:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void NoRussianTextIsLeftOutsideTheTranslationLayer()
    {
        var leftovers = new List<string>();
        foreach (var file in Sources("*.cs").Where(f => !f.EndsWith("Texts.cs", StringComparison.Ordinal)))
            foreach (var line in File.ReadLines(file))
            {
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal)) continue;
                var stripped = CodeKey().Replace(line, "");
                if (RussianLiteral().IsMatch(stripped)) leftovers.Add($"{Path.GetFileName(file)}: {line.Trim()}");
            }
        foreach (var file in Sources("*.xaml"))
            foreach (var line in File.ReadLines(file))
            {
                // Language names are always shown in their own language ("Русский", "English").
                if (line.Contains("GroupName=\"InterfaceLanguage\"", StringComparison.Ordinal)) continue;
                if (RussianAttribute().IsMatch(XamlKey().Replace(line, ""))) leftovers.Add($"{Path.GetFileName(file)}: {line.Trim()}");
            }
        Assert.True(leftovers.Count == 0, "Untranslated text:\n" + string.Join("\n", leftovers));
    }

    [Fact]
    public void SwitchingLanguageTranslatesAndFallsBack()
    {
        try
        {
            Texts.SetLanguage(Texts.English);
            Assert.Equal("Exit", Texts.T("Выход"));
            Assert.Equal("Group 3", Texts.F("Группа {0}", 3));
            Assert.Equal("нет такого ключа", Texts.T("нет такого ключа"));
            Texts.SetLanguage(Texts.Russian);
            Assert.Equal("Выход", Texts.T("Выход"));
        }
        finally { Texts.SetLanguage(Texts.Russian); }
    }

    [GeneratedRegex(@"Texts\.[TF]\(""((?:[^""\\]|\\.)*)""")]
    private static partial Regex CodeKey();
    [GeneratedRegex(@"\{l:Tr '([^']*)'\}")]
    private static partial Regex XamlKey();
    [GeneratedRegex(@"""(?:[^""\\]|\\.)*[Ѐ-ӿ](?:[^""\\]|\\.)*""")]
    private static partial Regex RussianLiteral();
    [GeneratedRegex(@"=""[^""]*[Ѐ-ӿ][^""]*""")]
    private static partial Regex RussianAttribute();
}
