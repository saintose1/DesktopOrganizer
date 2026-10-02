using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopOrganizer.App.Desktop;
using DesktopOrganizer.App.ViewModels;
using DesktopOrganizer.App.Views;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Infrastructure.Interop;
using DesktopOrganizer.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

internal static class RenderingSmoke
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
        var directory = Path.GetFullPath(Path.Combine(".tools", "rendering-smoke", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(directory);
        using var reader = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
        var initial = await reader.ReadAsync();
        if (!initial.Connected || !initial.CanPosition || initial.Error is not null) throw new InvalidOperationException(initial.Error ?? "No Explorer.");
        var probes = initial.Icons.Where(i => i.StableId.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)).Take(10).ToArray();
        if (probes.Length != 10) throw new InvalidOperationException("Ten real shortcuts are required.");
        var store = new JsonFenceStore(directory, NullLogger<JsonFenceStore>.Instance);
        var seed = new Fence(Guid.NewGuid(), "Rendering probe", 600, 120, 750, 650, false,
            probes.Select(i => new DesktopIconAssignment(i.StableId, i.DisplayName, 0, 0)));
        var empty = new Fence(Guid.NewGuid(), "Empty rendering probe", 1600, 120, 500, 500);
        seed = FenceIconLayout.Arrange(seed, initial.Metrics, 1.5); store.Save([seed, empty]);
        using var native = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
        var handles = new DesktopDiscovery(NullLogger<DesktopDiscovery>.Instance).Find()!;
        var originalPresentation = DesktopDiagnostics.IconVisibility(handles);
        var presentationPath = Path.Combine(directory, "desktop-presentation.json");
        using var host = new Win32DesktopHost(new(NullLogger<DesktopDiscovery>.Instance), NullLogger<Win32DesktopHost>.Instance, presentationPath);
        var measurements = new ScenarioLogger();
        using var desktop = new DesktopIntegrationService(host, new(), measurements, native,
            recoveryStore: new JsonIconRecoveryStore(directory, NullLogger<JsonIconRecoveryStore>.Instance));
        var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance, desktop);
        var window = new MainWindow(vm); window.Show(); Application.Current.MainWindow = window;
        IReadOnlyList<DesktopArea> monitors = [];
        try
        {
            desktop.Start(); monitors = host.GetMonitorAreas(); desktop.ShowDesktop(); await Task.Delay(300);
            await WaitPositions(vm.Fences[0]);
            Console.WriteLine(DesktopDiagnostics.IconVisibility(host.Handles!));
            Console.WriteLine(string.Join(Environment.NewLine, DesktopDiagnostics.Layers(host.Handles!)));
            Capture("initial");
            var notify = typeof(Win32DesktopHost).GetMethod("NotifyInteraction", BindingFlags.NonPublic | BindingFlags.Instance)!
                .CreateDelegate<Action<FenceInteractionEventArgs>>(host);
            var toggle = typeof(Win32DesktopHost).GetMethod("NotifyCollapse", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<Fence>>(host);
            var hover = typeof(Win32DesktopHost).GetMethod("NotifyVisualChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<Guid>>(host);
            var reconcile = typeof(DesktopIntegrationService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var coordinator = (DesktopIconCoordinator)typeof(DesktopIntegrationService).GetField("_icons", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(desktop)!;
            var protectedTransitions = 0;
            var verifyOverlap = false;
            coordinator.FramePresented += (_, _) =>
            {
                if (!verifyOverlap || !desktop.IsPresentingTransition) return;
                hover(vm.Fences[0].Id); reconcile.Invoke(desktop, [false]);
                var surface = DesktopDiagnostics.Inspect(host.Handles!).Single(s => s.Caption.EndsWith("Rendering probe", StringComparison.Ordinal));
                var expected = vm.Fences[0].IsCollapsed ? (int)vm.Fences[0].Height : (int)Math.Ceiling(FenceInteractionGeometry.HeaderHeight * host.GetScale(vm.Fences[0]));
                if (surface.Bounds.Height != expected) throw new InvalidOperationException("Hover/timer reverted an in-flight hide/show presentation.");
                protectedTransitions++;
            };
            var collapse = new List<double>(); var expand = new List<double>();
            for (var i = 0; i < 6; i++)
            {
                var original = vm.Fences[0]; var next = original.ToggleCollapsed(); var start = Stopwatch.GetTimestamp();
                toggle(original);
                await WaitPositions(next);
                (next.IsCollapsed ? collapse : expand).Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                for (var wait = 0; desktop.IsPresentingTransition && wait < 300; wait++) await Task.Delay(1);
                if (desktop.IsPresentingTransition || vm.Fences[0].IsCollapsed != next.IsCollapsed) throw new InvalidOperationException("Toggle commit did not complete.");
                if (i == 0) Capture("collapsed");
            }
            Console.WriteLine($"Ten real icons: collapse observed mean={collapse.Average():F3}ms; max={collapse.Max():F3}ms; expand mean={expand.Average():F3}ms; max={expand.Max():F3}ms");
            verifyOverlap = true;
            for (var i = 0; i < 6; i++)
            {
                var next = vm.Fences[0].ToggleCollapsed(); toggle(vm.Fences[0]); await WaitPositions(next);
                for (var wait = 0; desktop.IsPresentingTransition && wait < 300; wait++) await Task.Delay(1);
                if (desktop.IsPresentingTransition || vm.Fences[0].IsCollapsed != next.IsCollapsed) throw new InvalidOperationException("Concurrent hover/timer interrupted durable toggle commit.");
            }
            verifyOverlap = false;
            Console.WriteLine($"PASS: {protectedTransitions} real hide/show presentations survived concurrent hover/timer callbacks before durable commit");
            Console.WriteLine(DesktopDiagnostics.IconVisibility(host.Handles!));
            Capture("expanded");
            foreach (var groupIndex in new[] { 0, 1 })
            foreach (var resizing in new[] { false, true })
            {
                var original = vm.Fences[groupIndex]; var hit = resizing ? FenceHit.Right | FenceHit.Bottom : FenceHit.Header;
                var start = Stopwatch.GetTimestamp(); notify(new(original, InteractionPhase.Started) { Hit = hit });
                for (var i = 1; i <= 60; i++)
                {
                    // Abrupt back-and-forth updates expose stale footprints/regions more clearly than a slow straight drag.
                    var next = original.WithBounds(original.X + i * 4 + (i % 2 == 0 ? 0 : 100), original.Y + i * 2 + (i % 2 == 0 ? 0 : 70),
                        original.Width + (resizing ? i % 30 * 3 + (i % 2 == 0 ? 0 : 80) : 0), original.Height + (resizing ? i % 30 * 2 + (i % 2 == 0 ? 0 : 50) : 0));
                    notify(new(next, InteractionPhase.Preview) { Hit = hit });
                    await Task.Delay(4);
                    if (i is 20 or 40 or 60)
                    {
                        if (groupIndex == 0)
                        {
                            var actual = (await reader.ReadAsync()).Icons.First(icon => icon.StableId == probes[0].StableId);
                            Console.WriteLine($"{(resizing ? "resize" : "drag")}-{i}: Fence={next.X},{next.Y}; actualIcon={actual.Position}; bounds={actual.Bounds}");
                        }
                        Capture($"{(groupIndex == 0 ? "" : "empty-")}{(resizing ? "resize" : "drag")}-{i}", groupIndex == 0 ? null : new(1500, 70, 1000, 1000));
                    }
                }
                notify(new(original, InteractionPhase.Completed) { Hit = hit });
                for (var wait = 0; store.Load().Fences[groupIndex].X != original.X + 240 && wait < 300; wait++) await Task.Delay(1);
                if (groupIndex == 0) await WaitPositions(vm.Fences[0]);
                Console.WriteLine($"Real API {(groupIndex == 0 ? "ten icons" : "empty")} {(resizing ? "resize" : "drag")}: 60 updates in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1}ms");
            }
            Capture("final");
            toggle(vm.Fences[0]); await WaitPositions(vm.Fences[0].ToggleCollapsed());
            for (var wait = 0; desktop.IsPresentingTransition && wait < 300; wait++) await Task.Delay(1);
            if (!store.Load().Fences[0].IsCollapsed) throw new InvalidOperationException("Collapsed state not saved.");
            desktop.Dispose(); native.Dispose(); window.Close();
            if (DesktopDiagnostics.IconVisibility(handles) != originalPresentation || File.Exists(presentationPath))
                throw new InvalidOperationException("Explorer presentation properties or journal not restored on shutdown.");
            Console.WriteLine("PASS: original Explorer styles/colors restored and presentation journal removed");
            using (var restartedNative = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance))
            using (var restartedHost = new Win32DesktopHost(new(NullLogger<DesktopDiscovery>.Instance), NullLogger<Win32DesktopHost>.Instance, presentationPath))
            using (var restartedDesktop = new DesktopIntegrationService(restartedHost, new(), measurements, restartedNative,
                recoveryStore: new JsonIconRecoveryStore(directory, NullLogger<JsonIconRecoveryStore>.Instance)))
            {
                var restarted = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance, restartedDesktop);
                restartedDesktop.Start(); await Task.Delay(300);
                await WaitPositions(restarted.Fences[0]); Capture("restart-collapsed");
                if (!restarted.Fences[0].IsCollapsed || restarted.Fences[0].Icons.Length != 10) throw new InvalidOperationException("Restart lost collapsed assignments.");
                var again = typeof(Win32DesktopHost).GetMethod("NotifyCollapse", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<Fence>>(restartedHost);
                again(restarted.Fences[0]); await WaitPositions(restarted.Fences[0].ToggleCollapsed());
                for (var wait = 0; restartedDesktop.IsPresentingTransition && wait < 300; wait++) await Task.Delay(1);
                if (restarted.Fences[0].IsCollapsed) throw new InvalidOperationException("Expand after restart did not commit.");
                Capture("restart-expanded"); Console.WriteLine("PASS: persisted collapsed Fence restored, all ten real icons parked, then expanded to saved layout");
            }
            if (DesktopDiagnostics.IconVisibility(handles) != originalPresentation) throw new InvalidOperationException("Restart did not restore Explorer properties.");
            File.WriteAllText(Path.Combine(directory, "latency.json"), JsonSerializer.Serialize(new { collapse, expand, visualCollapse = measurements.Collapse, visualExpand = measurements.Expand, performance = measurements.Performance }));
            Console.WriteLine("Artifacts: " + directory);
        }
        finally
        {
            desktop.Dispose(); native.Dispose(); window.Close();
            await reader.PositionAsync(probes.Select(i => new DesktopIconTarget(i.StableId, i.Position)).ToArray());
            if (!IconPlacement.AtTargets(await reader.ReadAsync(), probes.Select(i => new DesktopIconTarget(i.StableId, i.Position)).ToArray()))
                throw new InvalidOperationException("Original positions not restored.");
            Console.WriteLine("PASS: all ten original shortcut positions restored");
        }
        async Task WaitPositions(Fence fence)
        {
            var targets = IconPlacement.Targets([fence], monitors, initial.Metrics);
            for (var i = 0; i < 150; i++)
            {
                if (IconPlacement.AtTargets(await reader.ReadAsync(), targets)) return;
                await Task.Delay(1);
            }
            throw new InvalidOperationException("Real positions did not reach targets.");
        }
        void Capture(string name, DesktopArea? captureArea = null)
        {
            var area = captureArea ?? new(550, 70, 1400, 1100);
            var pixels = DesktopDiagnostics.CaptureScreenArea(area);
            var bitmap = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgr32, null, pixels, area.Width * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
        }
    }
    private sealed class ScenarioLogger : ILogger<DesktopIntegrationService>
    {
        public List<double> Collapse { get; } = [];
        public List<double> Expand { get; } = [];
        public List<string> Performance { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, error);
            if (state is IEnumerable<KeyValuePair<string, object?>> properties && properties.FirstOrDefault(p => p.Key == "Latency").Value is double latency)
            {
                if (message.StartsWith("Collapse", StringComparison.Ordinal)) Collapse.Add(latency);
                else Expand.Add(latency);
            }
            if (message.Contains("performance", StringComparison.OrdinalIgnoreCase)) Performance.Add(message);
            if (error is not null || message.Contains("latency", StringComparison.Ordinal) || message.Contains("performance", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine(message + (error is null ? "" : " " + error));
        }
    }
}
