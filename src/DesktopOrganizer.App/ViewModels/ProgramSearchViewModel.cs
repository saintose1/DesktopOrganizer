using System.ComponentModel;
using System.IO;
using DesktopOrganizer.App.Commands;
using DesktopOrganizer.Core.Localization;
using DesktopOrganizer.Infrastructure;

namespace DesktopOrganizer.App.ViewModels;

public sealed class ProgramSearchViewModel : ObservableObject
{
    private readonly ProgramShortcutSearch _search;
    private IReadOnlyList<ProgramShortcut> _index = [];
    private IReadOnlyList<ProgramShortcut> _results = [];
    private string _query = "";
    private string _notice = "";
    private ProgramShortcut? _selected;
    public event Action? Opened;

    public ProgramSearchViewModel(ProgramShortcutSearch search)
    {
        _search = search;
        OpenCommand = new(Open, () => Selected is not null || Results.Count != 0);
    }

    public RelayCommand OpenCommand { get; }
    public IReadOnlyList<ProgramShortcut> Results { get => _results; private set => SetProperty(ref _results, value); }
    public ProgramShortcut? Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public string Query { get => _query; set { if (SetProperty(ref _query, value)) Filter(); } }
    public string Notice { get => _notice; private set => SetProperty(ref _notice, value); }

    public async Task LoadAsync()
    {
        Notice = Texts.T("Загрузка ярлыков…");
        _index = await Task.Run(_search.Load);
        Filter();
    }

    private void Filter()
    {
        Results = ProgramShortcutSearch.Match(_index, Query);
        Selected = Results.FirstOrDefault();
        Notice = Results.Count == 0 ? Texts.T("Совпадений нет") : "";
        OpenCommand.NotifyCanExecuteChanged();
    }

    private void Open()
    {
        if ((Selected ?? Results.FirstOrDefault()) is not { } item) return;
        try { _search.Open(item); Opened?.Invoke(); }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
        { Notice = Texts.T("Не удалось открыть ярлык."); }
    }
}
