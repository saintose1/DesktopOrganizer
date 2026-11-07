using DesktopOrganizer.Core.Localization;
using System.Windows;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using DesktopOrganizer.App.ViewModels;
using DesktopOrganizer.App.Views;
using DesktopOrganizer.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using DesktopOrganizer.App.Desktop;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Infrastructure.Interop;
using DesktopOrganizer.Infrastructure.Settings;

namespace DesktopOrganizer.App;

public partial class App : Application
{
    private IHost? _host;
    private ILogger<App>? _logger;
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = e.Args,
                ContentRootPath = AppContext.BaseDirectory
            });

            builder.Logging.ClearProviders();
            var dataDirectory = Path.GetFullPath(builder.Configuration["StorageDirectory"] ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopOrganizer"));
            // Interface language first, so even the earliest message boxes use it.
            try { Texts.SetLanguage(new JsonAppPreferences(dataDirectory, Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonAppPreferences>.Instance).Load().Language); }
            catch (Exception) { Texts.SetLanguage(null); } // unreadable settings: follow Windows; Settings reports the error
            var storageKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory.ToUpperInvariant())));
            _instanceMutex = new Mutex(true, $"Local\\DesktopOrganizer-{storageKey}", out _ownsMutex);
            var restoreHidden = bool.TryParse(builder.Configuration["RestoreHidden"], out var restore) && restore;
            var quiet = bool.TryParse(builder.Configuration["Quiet"], out var silent) && silent;
            if (!_ownsMutex)
            {
                if (!quiet) MessageBox.Show(restoreHidden
                    ? Texts.T("Сначала закройте DesktopOrganizer через трей → «Выход»: он сам вернёт скрытые ярлыки.")
                    : Texts.T("DesktopOrganizer уже запущен для этой папки данных."), "DesktopOrganizer");
                Shutdown(restoreHidden ? 2 : 0);
                return;
            }
            if (restoreHidden)
            {
                // Recovery without Explorer automation: un-hide every journaled item and exit (used by the uninstaller).
                var result = HiddenItemsRecovery.RestoreAll(dataDirectory);
                if (!quiet) MessageBox.Show(result.Failed.Count == 0
                    ? Texts.F("Скрытые ярлыки возвращены: {0}. Отсутствуют на диске: {1}.", result.Restored, result.Missing)
                    : Texts.F("Возвращено: {0}. Не удалось вернуть {1}:\n{2}", result.Restored, result.Failed.Count, string.Join("\n", result.Failed.Take(10))),
                    "DesktopOrganizer");
                Shutdown(result.Failed.Count == 0 ? 0 : 1);
                return;
            }
            builder.Services.AddInfrastructure(dataDirectory);
            builder.Services.AddSingleton<IStartupRegistration>(provider => new WindowsStartupRegistration(
                Path.Combine(AppContext.BaseDirectory, "DesktopOrganizer.App.exe"), provider.GetRequiredService<ILogger<WindowsStartupRegistration>>()));
            builder.Services.AddSingleton<SettingsViewModel>();
            builder.Services.AddSingleton<MainWindowViewModel>();
            builder.Services.AddTransient<MainWindow>(); // created on open, released on close-to-tray
            builder.Services.AddSingleton<Func<MainWindow>>(provider => provider.GetRequiredService<MainWindow>);
            builder.Services.AddSingleton<FenceBitmapRenderer>();
            builder.Services.AddSingleton<DesktopIntegrationService>();
            builder.Services.AddSingleton(provider => new WindowsTrayIcon(provider.GetRequiredService<ILogger<WindowsTrayIcon>>(),
                Path.Combine(AppContext.BaseDirectory, "Assets", "Branding", "tray.ico")));
            builder.Services.AddSingleton<TrayApplicationService>();
            builder.Services.AddSingleton<IDesktopIntegration>(provider => provider.GetRequiredService<DesktopIntegrationService>());

            _host = builder.Build();
            _logger = _host.Services.GetRequiredService<ILogger<App>>();
            await _host.StartAsync();

            var tray = _host.Services.GetRequiredService<TrayApplicationService>();
            tray.Start();
            if (!bool.TryParse(builder.Configuration["StartInTray"], out var startInTray) || !startInTray) tray.ShowWindow();
            _host.Services.GetRequiredService<DesktopIntegrationService>().Start();
            _logger.LogInformation("DesktopOrganizer started");
        }
        catch (Exception exception)
        {
            _logger?.LogCritical(exception, "DesktopOrganizer failed to start");
            MessageBox.Show(
                Texts.F("Не удалось запустить DesktopOrganizer.\n{0}", exception.Message),
                "DesktopOrganizer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _logger?.LogInformation("DesktopOrganizer shutting down");
            _host?.Services.GetService<MainWindowViewModel>()?.FlushAppearanceChanges();
            _host?.Services.GetService<TrayApplicationService>()?.Dispose();
            _host?.Services.GetService<DesktopIntegrationService>()?.Dispose();
            _host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "Error while stopping the application host");
        }
        finally
        {
            try { _host?.Dispose(); }
            catch (Exception exception) { _logger?.LogError(exception, "Resource cleanup failed"); }
            finally
            {
                if (_ownsMutex) _instanceMutex?.ReleaseMutex();
                _instanceMutex?.Dispose();
                base.OnExit(e);
            }
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _host?.Services.GetService<TrayApplicationService>()?.AllowSessionExit();
        base.OnSessionEnding(e);
    }
}
