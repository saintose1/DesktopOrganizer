using System.Collections.ObjectModel;
using System.Globalization;
using DesktopOrganizer.App.Commands;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Core.Storage;
using Microsoft.Extensions.Logging;
using DesktopOrganizer.Core.Desktop;
using System.Windows.Threading;

namespace DesktopOrganizer.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IFenceStore _store;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly ObservableCollection<Fence> _fences = [];
    private Fence? _selectedFence;
    private string _name = "", _x = "0", _y = "0", _width = "300", _height = "200";
    private string _status = "", _error = "";
    private bool _canWrite;
    private readonly IDesktopIntegration? _desktop;
    private Fence? _interactionOriginal;
    private bool _updatingFromDesktop;
    private bool _isSettingsPage;
    private Fence? _appearanceOriginal;
    private readonly DispatcherTimer _appearanceSaveTimer;
    public FenceAppearanceViewModel AppearanceEditor { get; }
    public SettingsViewModel? Settings { get; }
    public bool IsSettingsPage { get => _isSettingsPage; private set { if (SetProperty(ref _isSettingsPage, value)) OnPropertyChanged(nameof(IsFencesPage)); } }
    public bool IsFencesPage => !IsSettingsPage;
    public RelayCommand OpenFencesCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public string ConnectionLabel => _desktop?.Connection.Connected == true ? "Рабочий стол подключён" : "Подключение к рабочему столу…";
    public bool IsDesktopConnected => _desktop?.Connection.Connected == true;

    public string Title => "DesktopOrganizer — группы";
    public string DesktopStatus => $"Desktop integration: {(_desktop?.Connection.Connected == true ? "Connected" : "Disconnected")} · Surfaces: {_desktop?.Connection.SurfaceCount ?? 0}";
    public string IconStatus => _desktop?.Connection.IconStatus ?? "";
    public string DesktopDiagnostics => _desktop?.Connection.Diagnostics +
        (string.IsNullOrEmpty(_desktop?.Connection.Error) ? "" : $"\n{_desktop.Connection.Error}");
    public ReadOnlyObservableCollection<Fence> Fences { get; }
    public string? LoadWarning { get; }
    public bool HasLoadWarning => !string.IsNullOrEmpty(LoadWarning);
    public bool HasSelection => SelectedFence is not null;
    public bool CanEdit => HasSelection && _canWrite && _interactionOriginal is null && _desktop?.IsPresentingTransition != true;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Error
    {
        get => _error;
        private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public Fence? SelectedFence
    {
        get => _selectedFence;
        set
        {
            // ListBox temporarily clears selection when an immutable item is replaced.
            // Keep the editor draft during desktop preview; the replacement restores selection.
            if (_updatingFromDesktop && value is null) return;
            if (!_updatingFromDesktop && _selectedFence?.Id != value?.Id) FlushAppearanceChanges();
            if (SetProperty(ref _selectedFence, value))
            {
                ResetEditor();
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(CanEdit));
                ApplyCommand.NotifyCanExecuteChanged();
                DeleteCommand.NotifyCanExecuteChanged();
                SaveAutoSortCommand.NotifyCanExecuteChanged();
            }
        }
    }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string X { get => _x; set => SetProperty(ref _x, value); }
    public string Y { get => _y; set => SetProperty(ref _y, value); }
    public string Width { get => _width; set => SetProperty(ref _width, value); }
    public string Height { get => _height; set => SetProperty(ref _height, value); }
    private string _autoSort = "";
    /// <summary>File-name rules of the selected group, e.g. "*.png; *.lnk; Steam*".</summary>
    public string AutoSort { get => _autoSort; set => SetProperty(ref _autoSort, value); }
    public RelayCommand SaveAutoSortCommand { get; }
    public RelayCommand CreateCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ShowDesktopCommand { get; }

    public MainWindowViewModel(IFenceStore store, ILogger<MainWindowViewModel> logger, IDesktopIntegration? desktop = null, SettingsViewModel? settings = null)
    {
        _store = store;
        _logger = logger;
        _desktop = desktop;
        AppearanceEditor = new FenceAppearanceViewModel(PreviewAppearance);
        _appearanceSaveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(650) };
        _appearanceSaveTimer.Tick += OnAppearanceSaveTick;
        Settings = settings;
        OpenFencesCommand = new(() => IsSettingsPage = false);
        OpenSettingsCommand = new(() => { Settings?.Refresh(); IsSettingsPage = true; });
        Fences = new ReadOnlyObservableCollection<Fence>(_fences);
        CreateCommand = new RelayCommand(Create, () => _canWrite && _interactionOriginal is null && _desktop?.IsPresentingTransition != true);
        ApplyCommand = new RelayCommand(Apply, () => CanEdit);
        DeleteCommand = new RelayCommand(Delete, () => CanEdit);
        SaveAutoSortCommand = new RelayCommand(SaveAutoSort, () => CanEdit);
        ShowDesktopCommand = new RelayCommand(() => _desktop?.ShowDesktop(), () => _desktop?.Connection.Connected == true);
        var result = store.Load();
        _canWrite = result.CanWrite;
        _desktop?.SetInteractionEnabled(_canWrite);
        LoadWarning = result.Warning;
        foreach (var fence in result.Fences) _fences.Add(fence);
        _desktop?.SetFences(_fences.ToArray());
        _fences.CollectionChanged += (_, _) => { if (!_updatingFromDesktop) _desktop?.SetFences(_fences.ToArray()); };
        if (_desktop is not null) _desktop.ConnectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(DesktopStatus));
            OnPropertyChanged(nameof(ConnectionLabel));
            OnPropertyChanged(nameof(IsDesktopConnected));
            OnPropertyChanged(nameof(DesktopDiagnostics));
            OnPropertyChanged(nameof(IconStatus));
            ShowDesktopCommand.NotifyCanExecuteChanged();
            NotifyInteractionCommands();
        };
        if (_desktop is not null) _desktop.Interaction += OnDesktopInteraction;
        if (_desktop is not null) _desktop.StateChangeRequested += OnDesktopStateChange;
        SelectedFence = _fences.FirstOrDefault();
        Status = $"Загружено групп: {_fences.Count}. Изменения сохраняются автоматически.";
        logger.LogInformation("Main window initialized with {Count} groups", _fences.Count);
    }

    private void Create()
    {
        if (!FlushAppearanceChanges()) return;
        var number = 1;
        while (_fences.Any(fence => fence.Name == $"Группа {number}")) number++;
        var fence = new Fence(Guid.NewGuid(), $"Группа {number}", 0, 0, 300, 200);
        if (!TrySave(_fences.Append(fence).ToArray())) return;
        _fences.Add(fence);
        SelectedFence = fence;
    }

    private void Apply()
    {
        if (!FlushAppearanceChanges()) return;
        if (SelectedFence is null) return;
        Fence replacement;
        try
        {
            replacement = new Fence(SelectedFence.Id, Name,
                Parse(X, "X"), Parse(Y, "Y"), Parse(Width, "Ширина"), Parse(Height, "Высота"), SelectedFence.IsCollapsed, SelectedFence.Icons, SelectedFence.Appearance, SelectedFence.AutoSort);
            replacement = _desktop?.NormalizeFence(SelectedFence, replacement) ?? replacement;
        }
        catch (ArgumentException exception) { Error = exception.Message; return; }
        var index = _fences.IndexOf(SelectedFence);
        var snapshot = _fences.ToArray();
        snapshot[index] = replacement;
        if (!TrySave(snapshot)) return;
        _fences[index] = replacement;
        SelectedFence = replacement;
        ResetEditor();
    }

    private void SaveAutoSort()
    {
        if (!FlushAppearanceChanges() || SelectedFence is null) return;
        Fence replacement;
        try { replacement = SelectedFence.WithAutoSort(FenceAutoSort.Parse(AutoSort)); }
        catch (ArgumentException exception) { Error = exception.Message; return; }
        var index = _fences.IndexOf(SelectedFence);
        var snapshot = _fences.ToArray();
        snapshot[index] = replacement;
        if (!TrySave(snapshot)) return;
        _fences[index] = replacement;
        SelectedFence = replacement;
        _desktop?.SetFences(_fences.ToArray());
        Status = replacement.AutoSort.IsEmpty ? "Авто-сортировка выключена." : "Новые подходящие файлы будут попадать в эту группу.";
    }

    private void Delete()
    {
        if (!FlushAppearanceChanges()) return;
        if (SelectedFence is null) return;
        var removed = SelectedFence;
        var index = _fences.IndexOf(removed);
        if (!TrySave(_fences.Where(fence => fence.Id != removed.Id).ToArray())) return;
        _fences.Remove(removed);
        SelectedFence = _fences.Count == 0 ? null : _fences[Math.Min(index, _fences.Count - 1)];
    }

    private bool TrySave(IReadOnlyCollection<Fence> snapshot)
    {
        try
        {
            _store.Save(snapshot);
            Error = "";
            Status = $"Сохранено групп: {snapshot.Count}. {DateTime.Now:T}";
            return true;
        }
        catch (FenceStorageException exception)
        {
            _logger.LogError(exception, "Group command could not be persisted");
            Error = exception.Message;
            return false;
        }
    }

    private void OnDesktopInteraction(object? sender, FenceInteractionEventArgs args)
    {
        if (!_canWrite) return;
        var current = _fences.FirstOrDefault(f => f.Id == args.Fence.Id);
        if (current is null) return;
        if (args.Phase == InteractionPhase.Started)
        {
            _appearanceSaveTimer.Stop(); // Commit pending style with the completed gesture, never mid-drag.
            if (_interactionOriginal is not null) return;
            _interactionOriginal = current;
            NotifyInteractionCommands();
            return;
        }
        if (_interactionOriginal?.Id != args.Fence.Id) return;
        if (args.Phase == InteractionPhase.Preview)
        {
            _updatingFromDesktop = true;
            try { ReplaceFence(args.Fence); } finally { _updatingFromDesktop = false; }
            Status = "Изменение на рабочем столе. Сохранение после отпускания мыши.";
            return;
        }
        var original = _interactionOriginal;
        _interactionOriginal = null;
        NotifyInteractionCommands();
        if (args.Phase == InteractionPhase.Cancelled)
        {
            ReplaceFence(original);
            Status = "Изменение отменено.";
            FlushAppearanceChanges();
        }
        else if (current != original || _appearanceOriginal is not null)
        {
            if (!TrySave(_fences.ToArray()))
            {
                var error = Error;
                ReplaceFence(_appearanceOriginal ?? original);
                AppearanceEditor.Load(SelectedFence);
                Error = error;
            }
            _appearanceOriginal = null;
        }
        _desktop?.SetFences(_fences.ToArray());
    }

    private void OnDesktopStateChange(object? sender, FenceStateChangeRequest request)
    {
        if (!_canWrite || _interactionOriginal is not null || request.Fences.Count != _fences.Count) return;
        for (var i = 0; i < _fences.Count; i++) if (_fences[i].Id != request.Fences[i].Id) return;
        if (!TrySave(request.Fences.ToArray()))
        {
            if (_appearanceOriginal is { } original)
            {
                var error = Error; _appearanceOriginal = null; _appearanceSaveTimer.Stop();
                ReplaceFence(original); _desktop?.SetFences(_fences.ToArray()); Error = error;
            }
            return;
        }
        _appearanceOriginal = null; _appearanceSaveTimer.Stop();
        var selectedId = SelectedFence?.Id;
        _updatingFromDesktop = true;
        try
        {
            for (var i = 0; i < _fences.Count; i++)
                if (_fences[i] != request.Fences[i]) _fences[i] = request.Fences[i];
            SelectedFence = _fences.FirstOrDefault(f => f.Id == selectedId);
        }
        finally { _updatingFromDesktop = false; }
        request.Accepted = true;
        _desktop?.SetFences(_fences.ToArray());
    }

    private void ReplaceFence(Fence replacement)
    {
        var index = -1;
        for (var i = 0; i < _fences.Count; i++) if (_fences[i].Id == replacement.Id) { index = i; break; }
        if (index < 0 || _fences[index] == replacement) return;
        var selected = SelectedFence?.Id == replacement.Id;
        _fences[index] = replacement;
        if (selected)
        {
            if (_updatingFromDesktop)
            {
                _selectedFence = replacement; OnPropertyChanged(nameof(SelectedFence));
                X = Format(replacement.X); Y = Format(replacement.Y); Width = Format(replacement.Width); Height = Format(replacement.Height);
            }
            else SelectedFence = replacement;
        }
    }
    private void NotifyInteractionCommands()
    {
        OnPropertyChanged(nameof(CanEdit));
        CreateCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        SaveAutoSortCommand.NotifyCanExecuteChanged();
    }

    private void ResetEditor()
    {
        AppearanceEditor.Load(SelectedFence);
        Name = SelectedFence?.Name ?? "";
        AutoSort = FenceAutoSort.Format(SelectedFence?.AutoSort ?? []);
        X = Format(SelectedFence?.X ?? 0);
        Y = Format(SelectedFence?.Y ?? 0);
        Width = Format(SelectedFence?.Width ?? 300);
        Height = Format(SelectedFence?.Height ?? 200);
        Error = "";
    }
    private void PreviewAppearance(string name, FenceAppearance appearance)
    {
        if (!CanEdit || SelectedFence is null) return;
        var next = SelectedFence.WithPresentation(name, appearance);
        if (next == SelectedFence) return;
        _appearanceOriginal ??= SelectedFence;
        _updatingFromDesktop = true;
        try { ReplaceFence(next); Name = next.Name; }
        finally { _updatingFromDesktop = false; }
        _desktop?.SetFences(_fences.ToArray());
        Status = "Предпросмотр. Изменения сохраняются автоматически.";
        _appearanceSaveTimer.Stop(); _appearanceSaveTimer.Start();
    }
    private void OnAppearanceSaveTick(object? sender, EventArgs args) => FlushAppearanceChanges();
    public bool FlushAppearanceChanges()
    {
        if (_interactionOriginal is not null || _desktop?.IsPresentingTransition == true) return true;
        _appearanceSaveTimer.Stop();
        if (_appearanceOriginal is not { } original) return true;
        _appearanceOriginal = null;
        if (TrySave(_fences.ToArray())) return true;
        var error = Error;
        ReplaceFence(original);
        AppearanceEditor.Load(SelectedFence);
        _desktop?.SetFences(_fences.ToArray());
        Error = error;
        return false;
    }
    public void Dispose()
    {
        _appearanceSaveTimer.Stop();
        _appearanceSaveTimer.Tick -= OnAppearanceSaveTick;
    }
    private static string Format(double value) => value.ToString("G", CultureInfo.CurrentCulture);
    private static double Parse(string text, string field)
    {
        if (!(double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
              double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) || !double.IsFinite(value))
            throw new ArgumentException($"{field}: введите конечное число.");
        return value;
    }
}
