using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Windows;
using DesktopOrganizer.App.Desktop;
using DesktopOrganizer.App.ViewModels;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Infrastructure.Interop;
using DesktopOrganizer.Infrastructure.Settings;
using DesktopOrganizer.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

internal static class AnimationCadenceSmoke
{
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var result = 1;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await Scenario(); result = 0; }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { app.Shutdown(); }
        });
        app.Run(); return result;
    }
    private static async Task Scenario()
    {
        var directory = Path.GetFullPath(Path.Combine(".tools", "animation-cadence", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(directory);
        var store = new JsonFenceStore(directory, NullLogger<JsonFenceStore>.Instance);
        store.Save([new Fence(Guid.NewGuid(), "Cadence probe", 600, 120, 750, 650)]);
        using var host = new Win32DesktopHost(new(NullLogger<DesktopDiscovery>.Instance), NullLogger<Win32DesktopHost>.Instance,
            Path.Combine(directory, "desktop-presentation.json"));
        var settings = new SettingsViewModel(new ProbeStartup(), NullLogger<SettingsViewModel>.Instance,
            new JsonAppPreferences(directory, NullLogger<JsonAppPreferences>.Instance));
        using var desktop = new DesktopIntegrationService(host, new(), NullLogger<DesktopIntegrationService>.Instance, settings: settings);
        var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance, desktop, settings);
        desktop.Start();
        if (!desktop.Connection.Connected) throw new InvalidOperationException(desktop.Connection.Error);
        var toggle = typeof(Win32DesktopHost).GetMethod("NotifyCollapse", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<Fence>>(host);
        var animator = (FenceTransitionAnimator)typeof(DesktopIntegrationService).GetField("_animator", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(desktop)!;
        var renderer = (FenceBitmapRenderer)typeof(DesktopIntegrationService).GetField("_renderer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(desktop)!;
        var stableHwnd = DesktopDiagnostics.Inspect(host.Handles!).Single(s => s.Caption.EndsWith("Cadence probe", StringComparison.Ordinal)).Hwnd;
        var results = new List<object>();
        // No visible WPF window, activation, input simulation, capture or icon positioning.
        foreach (var seconds in new[] { 0.15, 0.25, 0.4 })
        foreach (var collapsed in new[] { true, false })
        {
            settings.ExpandAnimationSeconds = settings.CollapseAnimationSeconds = seconds;
            var initial = vm.Fences[0]; var present = host.BitmapPresentCount; var repaint = host.RepaintCount;
            var ulw = host.LayeredUpdateMilliseconds; var allocations = host.BitmapAllocationCount;
            var rasterizations = renderer.HeaderRasterizations;
            var geometryUpdates = host.HeightGeometryUpdates; var regionTime = host.HeightRegionMilliseconds;
            var started = Stopwatch.GetTimestamp(); toggle(initial);
            while (desktop.IsPresentingTransition && Stopwatch.GetElapsedTime(started).TotalSeconds < 5) await Task.Delay(1);
            if (desktop.IsPresentingTransition || vm.Fences[0].IsCollapsed != collapsed) throw new InvalidOperationException("Animation did not commit.");
            var count = host.BitmapPresentCount - present;
            var row = new { seconds, collapsed, animator.LastMetrics, GeometryUpdates = host.HeightGeometryUpdates - geometryUpdates,
                GeometryFps = (host.HeightGeometryUpdates - geometryUpdates) * animator.LastMetrics.Fps / animator.LastMetrics.Frames, UlwCount = count,
                UlwMilliseconds = host.LayeredUpdateMilliseconds - ulw, ExplorerRedraws = host.RepaintCount - repaint,
                RegionUpdateMilliseconds = host.HeightRegionMilliseconds - regionTime,
                BitmapAllocations = host.BitmapAllocationCount - allocations, HeaderRasterizations = renderer.HeaderRasterizations - rasterizations,
                ExplicitSetWindowPosCount = 0, ExplicitSetWindowPosMilliseconds = 0 };
            if (row.ExplorerRedraws != 0 || row.BitmapAllocations != 0 || row.HeaderRasterizations > 1 || count > animator.LastMetrics.Frames * 2 ||
                DesktopDiagnostics.Inspect(host.Handles!).Single(s => s.Caption.EndsWith("Cadence probe", StringComparison.Ordinal)).Hwnd != stableHwnd)
                throw new InvalidOperationException("Animation violated cached HWND/bitmap/render budgets.");
            results.Add(row); Console.WriteLine(JsonSerializer.Serialize(row));
        }
        File.WriteAllText(Path.Combine(directory, "cadence.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: three durations, stable HWND, no per-frame Explorer repaint or DIB allocation. API verification; physical gestures not tested.");
        Console.WriteLine("Artifacts: " + directory);
    }
    private sealed class ProbeStartup : IStartupRegistration
    { public bool IsEnabled() => false; public void SetEnabled(bool enabled) { } }
}
