using System.ComponentModel;
using System.Runtime;
using System.Windows;
using System.Windows.Threading;
using DesktopOrganizer.App.ViewModels;
using DesktopOrganizer.App.Views;
using DesktopOrganizer.Infrastructure.Interop;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.App.Desktop;

/// <summary>
/// Presentation adapter. The settings window exists only while it is shown: closing it to the tray releases
/// the WPF visual tree (most of the process memory); the DI-owned view models keep all state.
/// </summary>
public sealed class TrayApplicationService : IDisposable
{
    private readonly Func<MainWindow> _createWindow;
    private readonly MainWindowViewModel _viewModel;
    private readonly WindowsTrayIcon _tray;
    private readonly TrayLifetimeViewModel _lifetime;
    private readonly ILogger<TrayApplicationService> _logger;
    private MainWindow? _window;
    private bool _releasing;
    public TrayApplicationService(Func<MainWindow> createWindow, MainWindowViewModel viewModel, WindowsTrayIcon tray,
        DesktopIntegrationService desktop, ILogger<TrayApplicationService> logger)
    {
        _createWindow = createWindow; _viewModel = viewModel; _tray = tray; _logger = logger;
        _lifetime = new(Show, Release, async () =>
        {
            if (!_viewModel.FlushAppearanceChanges())
                throw new InvalidOperationException("Не удалось сохранить оформление группы. Повторите выход после устранения ошибки записи.");
            if (_window is not null) _window.IsEnabled = false;
            try { await desktop.PrepareExitAsync(); }
            finally { if (_window is not null) _window.IsEnabled = true; }
        }, () => Application.Current.Shutdown(), error =>
        {
            logger.LogError(error, "Graceful tray exit failed; application remains running");
            if (_window is { IsVisible: true } owner)
                MessageBox.Show(owner, "Не удалось завершить работу безопасно. Приложение продолжает работать.\n" + error.Message,
                    "DesktopOrganizer", MessageBoxButton.OK, MessageBoxImage.Error);
            else MessageBox.Show("Не удалось завершить работу безопасно. Приложение продолжает работать.\n" + error.Message,
                "DesktopOrganizer", MessageBoxButton.OK, MessageBoxImage.Error);
        });
    }
    public void Start()
    {
        _tray.Start(); // A usable tray icon is required before intercepting Close.
        _tray.OpenRequested += OnOpen;
        _tray.ExitRequested += OnExit;
    }
    public void ShowWindow() => _lifetime.Open();
    private void Show()
    {
        if (_window is null)
        {
            _window = _createWindow();
            _window.Closing += OnClosing;
            _window.Closed += OnClosed;
            Application.Current.MainWindow = _window;
        }
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate(); _window.Focus();
    }
    // Close-to-tray: the window is destroyed (not hidden) once the Closing event has returned.
    private void Release()
    {
        if (_window is null) return;
        _viewModel.FlushAppearanceChanges();
        _releasing = true;
        var window = _window;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(window.Close));
    }
    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_releasing) return;
        args.Cancel = !_lifetime.RequestWindowClose();
    }
    private void OnClosed(object? sender, EventArgs args)
    {
        if (sender is MainWindow window) { window.Closing -= OnClosing; window.Closed -= OnClosed; window.DataContext = null; }
        _window = null; _releasing = false;
        if (Application.Current is { } app && ReferenceEquals(app.MainWindow, sender)) app.MainWindow = null;
        // One compacting collection when the window is released, so the tray process gives the memory back.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        _logger.LogInformation("Settings window released to tray; managed heap {Megabytes:N0} MB", GC.GetTotalMemory(false) / 1048576.0);
    }
    private void OnOpen() => _lifetime.Open();
    private void OnExit() => Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
        new Action(async () => await _lifetime.ExitAsync())); // Finish native menu callback before destroying its owner.
    public void AllowSessionExit()
    {
        _viewModel.FlushAppearanceChanges();
        _lifetime.AllowSessionExit();
    }
    public void Dispose()
    {
        if (_window is not null) { _window.Closing -= OnClosing; _window.Closed -= OnClosed; }
        _tray.OpenRequested -= OnOpen; _tray.ExitRequested -= OnExit;
        _tray.Dispose();
    }
}
