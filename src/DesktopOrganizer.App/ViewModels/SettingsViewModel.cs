using System.ComponentModel;
using System.Diagnostics;
using DesktopOrganizer.Core.Localization;
using DesktopOrganizer.App.Commands;
using DesktopOrganizer.App.Themes;
using DesktopOrganizer.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly IStartupRegistration _startup;
    private readonly ILogger<SettingsViewModel> _logger;
    private bool _isEnabled;
    private readonly IAppPreferencesStore? _preferences;
    private AppPreferences _currentPreferences = new();
    public bool AutoExpandOnHover
    {
        get => _currentPreferences.AutoExpandOnHover;
        set
        {
            if (value == AutoExpandOnHover) return;
            try { SavePreferences(new(value, ExpandAnimationSeconds, CollapseAnimationSeconds, _currentPreferences.Language, _currentPreferences.Theme, _currentPreferences.ConcealCollapsedItems)); }
            catch (Exception error) { ReportPreferences(error); }
            OnPropertyChanged();
        }
    }
    public double ExpandAnimationSeconds
    {
        get => _currentPreferences.ExpandAnimationSeconds;
        set
        {
            if (value == ExpandAnimationSeconds) return;
            try { SavePreferences(new(AutoExpandOnHover, value, CollapseAnimationSeconds, _currentPreferences.Language, _currentPreferences.Theme, _currentPreferences.ConcealCollapsedItems)); }
            catch (Exception error) { ReportPreferences(error); }
            OnPropertyChanged();
        }
    }
    public double CollapseAnimationSeconds
    {
        get => _currentPreferences.CollapseAnimationSeconds;
        set
        {
            if (value == CollapseAnimationSeconds) return;
            try { SavePreferences(new(AutoExpandOnHover, ExpandAnimationSeconds, value, _currentPreferences.Language, _currentPreferences.Theme, _currentPreferences.ConcealCollapsedItems)); }
            catch (Exception error) { ReportPreferences(error); }
            OnPropertyChanged();
        }
    }
    public bool IsRussian { get => Texts.Language == Texts.Russian; set { if (value) SetLanguage(Texts.Russian); } }
    public bool IsEnglish { get => Texts.Language == Texts.English; set { if (value) SetLanguage(Texts.English); } }
    /// <summary>Hide icons even while the pointer remains over a just-collapsed group.</summary>
    public bool ConcealCollapsedItems
    {
        get => _currentPreferences.ConcealCollapsedItems;
        set
        {
            if (value == ConcealCollapsedItems) return;
            try { SavePreferences(new(AutoExpandOnHover, ExpandAnimationSeconds, CollapseAnimationSeconds, _currentPreferences.Language, _currentPreferences.Theme, value)); }
            catch (Exception error) { ReportPreferences(error); }
            OnPropertyChanged();
        }
    }
    public bool IsThemeSystem { get => _currentPreferences.Theme is null; set { if (value) SetTheme(null); } }
    public bool IsThemeLight { get => _currentPreferences.Theme == AppPreferences.LightTheme; set { if (value) SetTheme(AppPreferences.LightTheme); } }
    public bool IsThemeDark { get => _currentPreferences.Theme == AppPreferences.DarkTheme; set { if (value) SetTheme(AppPreferences.DarkTheme); } }
    private void SetTheme(string? theme)
    {
        if (_currentPreferences.Theme == theme) return;
        try { SavePreferences(new(AutoExpandOnHover, ExpandAnimationSeconds, CollapseAnimationSeconds, _currentPreferences.Language, theme, _currentPreferences.ConcealCollapsedItems)); }
        catch (Exception error) { ReportPreferences(error); }
        ThemeManager.Apply(_currentPreferences.Theme);
        OnPropertyChanged(nameof(IsThemeSystem)); OnPropertyChanged(nameof(IsThemeLight)); OnPropertyChanged(nameof(IsThemeDark));
    }
    private void SetLanguage(string language)
    {
        if (Texts.Language == language && _currentPreferences.Language == language) return;
        try { SavePreferences(new(AutoExpandOnHover, ExpandAnimationSeconds, CollapseAnimationSeconds, language, _currentPreferences.Theme, _currentPreferences.ConcealCollapsedItems)); }
        catch (Exception error) { ReportPreferences(error); }
        Texts.SetLanguage(language); // the window, tray menu and new messages switch at once
        OnPropertyChanged(nameof(IsRussian)); OnPropertyChanged(nameof(IsEnglish));
    }
    private void SavePreferences(AppPreferences preferences)
    {
        _preferences?.Save(preferences); _currentPreferences = preferences; Error = "";
    }
    private string _error = "";
    public string Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => Error.Length != 0;
    public bool IsStartupEnabled
    {
        get => _isEnabled;
        set
        {
            if (value == _isEnabled) return;
            try { _startup.SetEnabled(value); _isEnabled = _startup.IsEnabled(); Error = ""; }
            catch (Exception error) { Report(error); }
            // Revert the toggle on failed writes, even when the backing field did not change.
            OnPropertyChanged();
        }
    }
    private string? _feedbackError;
    public string? FeedbackError { get => _feedbackError; private set => SetProperty(ref _feedbackError, value); }
    public RelayCommand ReportBugCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public SettingsViewModel(IStartupRegistration startup, ILogger<SettingsViewModel> logger, IAppPreferencesStore? preferences = null)
    {
        _startup = startup; _logger = logger; _preferences = preferences;
        ReportBugCommand = new(OpenBugReport);
        RefreshCommand = new(Refresh);
        Refresh();
    }
    public void Refresh()
    {
        try { _isEnabled = _startup.IsEnabled(); Error = ""; OnPropertyChanged(nameof(IsStartupEnabled)); }
        catch (Exception error) { Report(error); }
        try
        {
            if (_preferences is not null) _currentPreferences = _preferences.Load();
            OnPropertyChanged(nameof(AutoExpandOnHover));
            OnPropertyChanged(nameof(ExpandAnimationSeconds));
            OnPropertyChanged(nameof(CollapseAnimationSeconds));
            OnPropertyChanged(nameof(IsThemeSystem)); OnPropertyChanged(nameof(IsThemeLight)); OnPropertyChanged(nameof(IsThemeDark));
            OnPropertyChanged(nameof(ConcealCollapsedItems));
        }
        catch (Exception error) { ReportPreferences(error); }
    }
    private void ReportPreferences(Exception error)
    {
        _logger.LogError(error, "Could not read or update application preferences");
        Error = Texts.T("Не удалось прочитать или сохранить настройки. ") + error.Message;
    }
    private void Report(Exception error)
    {
        _logger.LogError(error, "Could not read or update current-user autostart");
        Error = Texts.T("Не удалось изменить или прочитать автозапуск. ") + error.Message;
    }
    private void OpenBugReport()
    {
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(
                "https://github.com/saintose1/DesktopOrganizer/issues/new?template=bug_report.yml")
            {
                UseShellExecute = true
            });
            FeedbackError = null;
        }
        catch (Win32Exception error)
        {
            _logger.LogWarning(error, "Could not open the GitHub bug report form");
            FeedbackError = Texts.T("Не удалось открыть браузер. Форму можно найти на GitHub во вкладке Issues.");
        }
    }
}
