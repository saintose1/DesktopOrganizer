using System.IO;
using System.Collections.ObjectModel;
using DesktopOrganizer.App.Commands;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Localization;

namespace DesktopOrganizer.App.ViewModels;

/// <summary>One auto-sort mask of one group, as listed on the Rules page.</summary>
public sealed record RuleRow(string Mask, Guid FenceId, string FenceName);

/// <summary>
/// Rules page: a flat view of the auto-sort masks stored in the groups (see <see cref="FenceAutoSort"/>).
/// Adding or removing a rule edits the masks of the owning group through the normal save-then-update path.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private const int PreviewLimit = 5;
    private string _newRuleMask = "";
    private Guid _newRuleFenceId;
    private string[] _desktopNames = [];
    private string _rulePreviewSummary = "";
    public ObservableCollection<RuleRow> Rules { get; } = [];
    public ObservableCollection<string> RulePreview { get; } = [];
    public RelayCommand AddRuleCommand { get; }
    public ParameterCommand RemoveRuleCommand { get; }
    private ParameterCommand? _useMask;
    /// <summary>A ready-made mask from the Rules page ("*.exe; *.msi") replaces the typed one.</summary>
    public ParameterCommand UseMaskCommand => _useMask ??= new(parameter => { if (parameter is string mask) NewRuleMask = mask; });
    public bool CanEditRules => _canWrite && _interactionOriginal is null && _desktop?.IsPresentingTransition != true;
    public bool HasRules => Rules.Count > 0;
    public bool HasNoRules => Rules.Count == 0;
    public string RulePreviewSummary { get => _rulePreviewSummary; private set => SetProperty(ref _rulePreviewSummary, value); }
    public string NewRuleMask
    {
        get => _newRuleMask;
        set { if (SetProperty(ref _newRuleMask, value)) { UpdateRulePreview(); AddRuleCommand.NotifyCanExecuteChanged(); } }
    }
    /// <summary>Group chosen for the new rule. Null arrives while the list is being rebuilt and is ignored.</summary>
    public Guid? NewRuleFenceId
    {
        get => _newRuleFenceId;
        set { if (value is { } id && SetProperty(ref _newRuleFenceId, id)) AddRuleCommand.NotifyCanExecuteChanged(); }
    }

    private void RefreshRules()
    {
        Rules.Clear();
        foreach (var fence in _fences)
            foreach (var mask in fence.AutoSort) Rules.Add(new RuleRow(mask, fence.Id, fence.Name));
        if (_fences.All(f => f.Id != _newRuleFenceId)) NewRuleFenceId = _fences.FirstOrDefault()?.Id ?? Guid.Empty;
        OnPropertyChanged(nameof(NewRuleFenceId)); // the group list was rebuilt: show the current choice again
        _desktopNames = ReadDesktopNames();
        OnPropertyChanged(nameof(HasRules)); OnPropertyChanged(nameof(HasNoRules));
        UpdateRulePreview();
        NotifyRuleCommands();
    }

    private void NotifyRuleCommands()
    {
        OnPropertyChanged(nameof(CanEditRules));
        AddRuleCommand.NotifyCanExecuteChanged();
        RemoveRuleCommand.NotifyCanExecuteChanged();
    }

    private void AddRule()
    {
        if (!FlushAppearanceChanges()) return;
        var target = _fences.FirstOrDefault(f => f.Id == _newRuleFenceId);
        if (target is null) { Error = Texts.T("Выберите группу для правила."); return; }
        IReadOnlyList<string> added;
        try { added = FenceAutoSort.Parse(NewRuleMask); }
        catch (ArgumentException exception) { Error = exception.Message; return; }
        if (added.Count == 0) { Error = Texts.T("Введите маску, например *.pdf"); return; }
        var merged = target.AutoSort.Concat(added).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        try { if (!ApplyAutoSort(target, merged)) return; }
        catch (ArgumentException exception) { Error = exception.Message; return; }
        Error = "";
        NewRuleMask = "";
        RefreshRules();
    }

    private void RemoveRule(object? parameter)
    {
        if (parameter is not RuleRow row || !FlushAppearanceChanges()) return;
        var target = _fences.FirstOrDefault(f => f.Id == row.FenceId);
        if (target is null) return;
        if (ApplyAutoSort(target, target.AutoSort.Where(m => !string.Equals(m, row.Mask, StringComparison.OrdinalIgnoreCase)).ToArray())) RefreshRules();
    }

    private void UpdateRulePreview()
    {
        RulePreview.Clear();
        IReadOnlyList<string> patterns;
        try { patterns = FenceAutoSort.Parse(NewRuleMask); }
        catch (ArgumentException) { patterns = []; }
        if (patterns.Count == 0) { RulePreviewSummary = Texts.T("Введите маску, чтобы увидеть подходящие файлы на рабочем столе."); return; }
        var matches = _desktopNames.Where(name => patterns.Any(p => FenceAutoSort.Matches(name, p))).ToArray();
        foreach (var name in matches.Take(PreviewLimit)) RulePreview.Add(name);
        RulePreviewSummary = MatchesText(matches.Length);
    }

    /// <summary>File and folder names on the user and public desktops; read on demand, never watched.</summary>
    private static string[] ReadDesktopNames()
    {
        var names = new List<string>();
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            try
            {
                var path = Environment.GetFolderPath(folder);
                if (path.Length == 0 || !Directory.Exists(path)) continue;
                foreach (var entry in Directory.EnumerateFileSystemEntries(path))
                {
                    var name = Path.GetFileName(entry);
                    if (name.Length != 0 && !string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)) names.Add(name);
                    if (names.Count >= 5000) return [.. names];
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return [.. names];
    }
}
