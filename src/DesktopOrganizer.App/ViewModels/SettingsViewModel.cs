using DesktopOrganizer.App.Commands;
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
            try { SavePreferences(new(value, ExpandAnimationSeconds, CollapseAnimationSeconds)); }
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
            try { SavePreferences(new(AutoExpandOnHover, value, CollapseAnimationSeconds)); }
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
            try { SavePreferences(new(AutoExpandOnHover, ExpandAnimationSeconds, value)); }
            catch (Exception error) { ReportPreferences(error); }
            OnPropertyChanged();
        }
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
    public RelayCommand RefreshCommand { get; }
    public SettingsViewModel(IStartupRegistration startup, ILogger<SettingsViewModel> logger, IAppPreferencesStore? preferences = null)
    {
        _startup = startup; _logger = logger; _preferences = preferences; RefreshCommand = new(Refresh); Refresh();
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
        }
        catch (Exception error) { ReportPreferences(error); }
    }
    private void ReportPreferences(Exception error)
    {
        _logger.LogError(error, "Could not read or update application preferences");
        Error = "Не удалось прочитать или сохранить настройки. " + error.Message;
    }
    private void Report(Exception error)
    {
        _logger.LogError(error, "Could not read or update current-user autostart");
        Error = "Не удалось изменить или прочитать автозапуск. " + error.Message;
    }
}
