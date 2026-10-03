using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using DesktopOrganizer.App.ViewModels;
using DesktopOrganizer.App.Views;
using DesktopOrganizer.Infrastructure.Interop;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.App.Desktop;

/// <summary>Presentation adapter; one DI-owned MainWindow throughout the process lifetime.</summary>
public sealed class TrayApplicationService : IDisposable
{
    private readonly MainWindow _window;
    private readonly WindowsTrayIcon _tray;
    private readonly TrayLifetimeViewModel _lifetime;
    public TrayApplicationService(MainWindow window, WindowsTrayIcon tray, DesktopIntegrationService desktop, ILogger<TrayApplicationService> logger)
    {
        _window = window; _tray = tray;
        _lifetime = new(Show, window.Hide, async () =>
        {
            window.IsEnabled = false;
            try { await desktop.PrepareExitAsync(); }
            finally { window.IsEnabled = true; }
        }, () => Application.Current.Shutdown(), error =>
        {
            logger.LogError(error, "Graceful tray exit failed; application remains running");
            MessageBox.Show(window, "Не удалось завершить работу безопасно. Приложение продолжает работать.\n" + error.Message,
                "DesktopOrganizer", MessageBoxButton.OK, MessageBoxImage.Error);
        });
    }
    public void Start()
    {
        _tray.Start(); // A usable tray icon is required before intercepting Close.
        _tray.OpenRequested += OnOpen;
        _tray.ExitRequested += OnExit;
        _window.Closing += OnClosing;
    }
    private void Show()
    {
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate(); _window.Focus();
    }
    private void OnOpen() => _lifetime.Open();
    private void OnExit() => _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
        new Action(async () => await _lifetime.ExitAsync())); // Finish native menu callback before destroying its owner.
    private void OnClosing(object? sender, CancelEventArgs args) => args.Cancel = !_lifetime.RequestWindowClose();
    public void AllowSessionExit() => _lifetime.AllowSessionExit();
    public void Dispose()
    {
        _window.Closing -= OnClosing; _tray.OpenRequested -= OnOpen; _tray.ExitRequested -= OnExit;
        _tray.Dispose();
    }
}
