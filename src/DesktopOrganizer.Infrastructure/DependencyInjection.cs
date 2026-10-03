using Microsoft.Extensions.DependencyInjection;
using Serilog;
using DesktopOrganizer.Core.Storage;
using DesktopOrganizer.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using DesktopOrganizer.Infrastructure.Interop;
using System.Runtime.Versioning;

namespace DesktopOrganizer.Infrastructure;

public static class DependencyInjection
{
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string? dataDirectory = null)
    {
        dataDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopOrganizer");
        var storageDirectory = Path.GetFullPath(dataDirectory);
        var logDirectory = Path.Combine(storageDirectory, "Logs");
        services.AddSingleton<IFenceStore>(provider => new JsonFenceStore(
            storageDirectory, provider.GetRequiredService<ILogger<JsonFenceStore>>()));
        services.AddSingleton<DesktopDiscovery>();
        services.AddSingleton<ExplorerInteractionTrace>();
        services.AddSingleton<IIconRecoveryStore>(p => new JsonIconRecoveryStore(storageDirectory, p.GetRequiredService<ILogger<JsonIconRecoveryStore>>()));
        services.AddSingleton(p => new Win32DesktopHost(p.GetRequiredService<DesktopDiscovery>(),
            p.GetRequiredService<ILogger<Win32DesktopHost>>(), Path.Combine(storageDirectory, "desktop-presentation.json"),
            interactionTrace: p.GetRequiredService<ExplorerInteractionTrace>()));
        services.AddSingleton<DesktopOrganizer.Core.Desktop.IDesktopIconService, DesktopIconService>();

        services.AddSerilog((_, configuration) => configuration
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logDirectory, "desktop-organizer-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true));

        return services;
    }
}
