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
            var storageKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory.ToUpperInvariant())));
            _instanceMutex = new Mutex(true, $"Local\\DesktopOrganizer-{storageKey}", out _ownsMutex);
            if (!_ownsMutex)
            {
                MessageBox.Show("DesktopOrganizer уже запущен для этой папки данных.", "DesktopOrganizer");
                Shutdown();
                return;
            }
            builder.Services.AddInfrastructure(dataDirectory);
            builder.Services.AddSingleton<MainWindowViewModel>();
            builder.Services.AddSingleton<MainWindow>();
            builder.Services.AddSingleton<FenceBitmapRenderer>();
            builder.Services.AddSingleton<DesktopIntegrationService>();
            builder.Services.AddSingleton<WindowsTrayIcon>();
            builder.Services.AddSingleton<TrayApplicationService>();
            builder.Services.AddSingleton<IDesktopIntegration>(provider => provider.GetRequiredService<DesktopIntegrationService>());

            _host = builder.Build();
            _logger = _host.Services.GetRequiredService<ILogger<App>>();
            await _host.StartAsync();

            MainWindow = _host.Services.GetRequiredService<MainWindow>();
            _host.Services.GetRequiredService<TrayApplicationService>().Start();
            MainWindow.Show();
            _host.Services.GetRequiredService<DesktopIntegrationService>().Start();
            _logger.LogInformation("DesktopOrganizer started");
        }
        catch (Exception exception)
        {
            _logger?.LogCritical(exception, "DesktopOrganizer failed to start");
            MessageBox.Show(
                $"Не удалось запустить DesktopOrganizer.\n{exception.Message}",
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
