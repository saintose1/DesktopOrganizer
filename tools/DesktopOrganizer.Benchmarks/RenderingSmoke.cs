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
using DesktopOrganizer.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

internal static class RenderingSmoke
{
    public static int Run(bool showDesktop = true)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var result = 1;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DesktopOrganizer.App;component/Themes/LavenderHud.xaml", UriKind.Relative) });
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await Scenario(showDesktop); result = 0; }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { app.Shutdown(); }
        });
        app.Run(); return result;
    }
    private static async Task Scenario(bool showDesktop)
    {
        var directory = Path.GetFullPath(Path.Combine(".tools", "rendering-smoke", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(directory);
        using var reader = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
        var initial = await reader.ReadAsync();
        if (!initial.Connected || !initial.CanPosition || initial.Error is not null) throw new InvalidOperationException(initial.Error ?? "No Explorer.");
        File.WriteAllText(Path.Combine(directory, "original-positions.json"), JsonSerializer.Serialize(initial.Icons.Select(i => new DesktopIconTarget(i.StableId, i.Position))));
        var probes = initial.Icons.Where(i => i.StableId.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)).Take(12).ToArray();
        if (probes.Length != 12) throw new InvalidOperationException("Twelve real shortcuts are required.");
        var store = new JsonFenceStore(directory, NullLogger<JsonFenceStore>.Instance);
        var seed = new Fence(Guid.NewGuid(), "Rendering probe", 600, 120, 750, 650, false,
            probes.Select(i => new DesktopIconAssignment(i.StableId, i.DisplayName, 0, 0)));
        var empty = new Fence(Guid.NewGuid(), "Empty rendering probe", 1600, 120, 500, 500);
        seed = FenceIconLayout.Arrange(seed, initial.Metrics, 1.5); store.Save([seed, empty]);
        using var native = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
        var handles = new DesktopDiscovery(NullLogger<DesktopDiscovery>.Instance).Find()!;
        var originalPresentation = DesktopDiagnostics.IconVisibility(handles);
        var originalRegion = DesktopDiagnostics.IconRegion(handles);
        var presentationPath = Path.Combine(directory, "desktop-presentation.json");
        using var host = new Win32DesktopHost(new(NullLogger<DesktopDiscovery>.Instance), NullLogger<Win32DesktopHost>.Instance, presentationPath);
        var measurements = new ScenarioLogger();
        var preferenceStore = new JsonAppPreferences(directory, NullLogger<JsonAppPreferences>.Instance);
        var settings = new SettingsViewModel(new ProbeStartup(), NullLogger<SettingsViewModel>.Instance, preferenceStore);
        using var desktop = new DesktopIntegrationService(host, new(), measurements, native,
            recoveryStore: new JsonIconRecoveryStore(directory, NullLogger<JsonIconRecoveryStore>.Instance), settings: settings);
        var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance, desktop, settings);
        var window = new MainWindow(vm); if (showDesktop) window.Show(); Application.Current.MainWindow = window;
        IReadOnlyList<DesktopArea> monitors = [];
        try
        {
            desktop.Start(); monitors = host.GetMonitorAreas(); if (showDesktop) desktop.ShowDesktop(); await Task.Delay(300);
            await WaitPositions(vm.Fences[0]);
            Console.WriteLine(DesktopDiagnostics.IconVisibility(host.Handles!));
            Console.WriteLine(string.Join(Environment.NewLine, DesktopDiagnostics.Layers(host.Handles!)));
            Capture("initial");
            var stableHwnd = DesktopDiagnostics.Inspect(host.Handles!).Single(s => s.Caption.EndsWith("Rendering probe", StringComparison.Ordinal)).Hwnd;
            var originalIconPositions = (await reader.ReadAsync()).Icons.ToDictionary(i => i.StableId, i => i.Position);
            vm.AppearanceEditor.FontFamily = "Consolas"; vm.AppearanceEditor.FontSize = "22";
            vm.AppearanceEditor.Bold = true; vm.AppearanceEditor.Italic = true;
            vm.AppearanceEditor.TextColor = "#CDAAFF"; vm.AppearanceEditor.HeaderColor = "#553366"; vm.AppearanceEditor.HeaderOpacity = 65;
            vm.AppearanceEditor.BodyColor = "#372342"; vm.AppearanceEditor.BodyOpacity = 30;
            vm.AppearanceEditor.BorderColor = "#AD8FE8"; vm.AppearanceEditor.BorderOpacity = 75;
            foreach (var alignment in Enum.GetValues<FenceTitleAlignment>())
            {
                vm.AppearanceEditor.Alignment = alignment;
                if (DesktopDiagnostics.Inspect(host.Handles!).Single(s => s.Caption.EndsWith("Rendering probe", StringComparison.Ordinal)).Hwnd != stableHwnd)
                    throw new InvalidOperationException("Appearance update recreated the Fence HWND.");
                Capture("appearance-" + alignment);
            }
            vm.FlushAppearanceChanges();
            var styledAppearance = vm.Fences[0].Appearance;
            if ((await reader.ReadAsync()).Icons.Any(i => originalIconPositions[i.StableId] != i.Position))
                throw new InvalidOperationException("Style-only updates moved real desktop icons.");
            Console.WriteLine("PASS: live appearance, three alignments, stable HWND; no real icon positions changed");
            var notify = typeof(Win32DesktopHost).GetMethod("NotifyInteraction", BindingFlags.NonPublic | BindingFlags.Instance)!
                .CreateDelegate<Action<FenceInteractionEventArgs>>(host);
            var toggle = typeof(Win32DesktopHost).GetMethod("NotifyCollapse", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<Fence>>(host);
            var hover = typeof(Win32DesktopHost).GetMethod("NotifyVisualChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<Guid>>(host);
            var reconcile = typeof(DesktopIntegrationService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var coordinator = (DesktopIconCoordinator)typeof(DesktopIntegrationService).GetField("_icons", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(desktop)!;
            var animator = (FenceTransitionAnimator)typeof(DesktopIntegrationService).GetField("_animator", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(desktop)!;
            var requestState = typeof(DesktopIntegrationService).GetMethod("RequestStateChange", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Func<IReadOnlyList<Fence>, bool>>(desktop);
            var animationBatches = 0;
            var animationReports = new List<object>();
            void CountBatch(long requestedAt, double elapsed) => animationBatches++;
            coordinator.FramePresented += CountBatch;
            foreach (var iconCount in new[] { 1, 5, 12 })
            {
            var assigned = vm.Fences.ToArray(); assigned[0] = seed.WithPresentation(seed.Name, styledAppearance).WithIcons(seed.Icons.Take(iconCount));
            if (!requestState(assigned)) throw new InvalidOperationException("Cannot prepare icon-count scenario.");
            await WaitPositions(vm.Fences[0]);
            foreach (var seconds in new[] { 0.15, 0.25, 0.4 })
            foreach (var collapsed in new[] { true, false })
            {
                settings.ExpandAnimationSeconds = settings.CollapseAnimationSeconds = seconds;
                var before = vm.Fences[0]; var desired = before.ToggleCollapsed();
                var normalHeight = before.Height; var started = Stopwatch.GetTimestamp();
                var counters = native.PositionCounters;
                int? shellCompletionHeight = null;
                animationBatches = 0; toggle(before);
                var heights = new List<int>();
                while (desktop.IsPresentingTransition && Stopwatch.GetElapsedTime(started).TotalSeconds < 5)
                {
                    var surface = DesktopDiagnostics.Inspect(host.Handles!).Single(s => s.Caption.EndsWith("Rendering probe", StringComparison.Ordinal));
                    if (surface.Hwnd != stableHwnd) throw new InvalidOperationException("Animation recreated the native surface.");
                    if (DesktopDiagnostics.IconRegion(host.Handles!) != originalRegion)
                        throw new InvalidOperationException("Animation modified the Explorer window region.");
                    heights.Add(surface.Bounds.Height);
                    if (native.PositionCounters.RedrawCompletions > counters.RedrawCompletions)
                        shellCompletionHeight ??= surface.Bounds.Height;
                    if (vm.Fences[0].Height != normalHeight) throw new InvalidOperationException("Animation changed normal model Height.");
                    await Task.Delay(10);
                }
                var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
                var headerHeight = (int)Math.Ceiling(FenceInteractionGeometry.HeaderHeight * host.GetScale(before));
                if (desktop.IsPresentingTransition || vm.Fences[0].IsCollapsed != collapsed ||
                    !heights.Any(h => h > headerHeight && h < normalHeight) || animationBatches < 2 || animationBatches > animator.LastMetrics.Frames ||
                    elapsed < (collapsed ? settings.CollapseAnimationSeconds : settings.ExpandAnimationSeconds))
                    throw new InvalidOperationException("Animated transition failed shared-frame geometry, duration or commit checks.");
                var batches = native.PositionCounters.Batches - counters.Batches;
                var redraws = native.PositionCounters.RedrawCompletions - counters.RedrawCompletions;
                if (batches < 2 || batches > animator.LastMetrics.Frames || redraws != 1)
                    throw new InvalidOperationException("Animation violated one bulk per frame / one final Shell completion.");
                if (collapsed && !(shellCompletionHeight > headerHeight))
                    throw new InvalidOperationException("Icon hide waited until after the collapsed Fence presentation.");
                await WaitPositions(desired);
                if (DesktopDiagnostics.IconRegion(host.Handles!) != originalRegion) throw new InvalidOperationException("Animation modified the Explorer window region.");
                animationReports.Add(new { iconCount, seconds, collapsed, observed = elapsed, animator.LastMetrics, bulkUpdates = batches, shellCompletions = redraws, shellCompletionHeight });
                Console.WriteLine($"PASS: coupled {(collapsed ? "collapse" : "expand")}: icons={iconCount}; duration={seconds:F2}s; observed={elapsed:F3}s; cadence={animator.LastMetrics.Fps:F1}fps; interval avg/max={animator.LastMetrics.AverageIntervalMs:F2}/{animator.LastMetrics.MaximumIntervalMs:F2}ms; coalesced={animator.LastMetrics.CoalescedFrames}; frames={animator.LastMetrics.Frames}; bulk/Shell commits={batches}/{redraws}; Explorer region unchanged throughout; stable HWND; normal Height unchanged");
            }
            }
            coordinator.FramePresented -= CountBatch;
            var tenIcons = vm.Fences.ToArray(); tenIcons[0] = tenIcons[0].WithIcons(seed.Icons.Take(10));
            if (!requestState(tenIcons)) throw new InvalidOperationException("Cannot prepare ten-icon lifecycle scenario.");
            await WaitPositions(vm.Fences[0]);
            settings.ExpandAnimationSeconds = 0; settings.CollapseAnimationSeconds = 0;
            foreach (var collapsed in new[] { true, false })
            {
                toggle(vm.Fences[0]);
                for (var wait = 0; desktop.IsPresentingTransition && wait < 500; wait++) await Task.Delay(1);
                if (desktop.IsPresentingTransition || vm.Fences[0].IsCollapsed != collapsed) throw new InvalidOperationException("Zero-duration transition did not commit.");
            }
            settings.ExpandAnimationSeconds = 0.18; settings.CollapseAnimationSeconds = 0.14;
            Console.WriteLine("PASS: zero-duration collapse/expand and persisted independent animation settings");
            var protectedTransitions = 0;
            var verifyOverlap = false;
            coordinator.FramePresented += (_, _) =>
            {
                if (!verifyOverlap || !desktop.IsPresentingTransition) return;
                var beforeBounds = DesktopDiagnostics.Inspect(host.Handles!).Single(s => s.Caption.EndsWith("Rendering probe", StringComparison.Ordinal)).Bounds;
                hover(vm.Fences[0].Id); reconcile.Invoke(desktop, [false]);
                var surface = DesktopDiagnostics.Inspect(host.Handles!).Single(s => s.Caption.EndsWith("Rendering probe", StringComparison.Ordinal));
                if (surface.Bounds != beforeBounds) throw new InvalidOperationException("Hover/timer reverted an in-flight animated hide/show presentation.");
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
                            var actual = (await reader.ReadAsync()).Icons.FirstOrDefault(icon => icon.StableId == probes[0].StableId);
                            if (actual is not null) Console.WriteLine($"{(resizing ? "resize" : "drag")}-{i}: Fence={next.X},{next.Y}; actualIcon={actual.Position}; bounds={actual.Bounds}");
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
            if (DesktopDiagnostics.IconVisibility(handles) != originalPresentation || DesktopDiagnostics.IconRegion(handles) != originalRegion || File.Exists(presentationPath))
                throw new InvalidOperationException("Explorer presentation properties or journal not restored on shutdown.");
            Console.WriteLine("PASS: original Explorer styles/colors/window region restored and presentation journal removed");
            using (var restartedNative = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance))
            using (var restartedHost = new Win32DesktopHost(new(NullLogger<DesktopDiscovery>.Instance), NullLogger<Win32DesktopHost>.Instance, presentationPath))
            using (var restartedDesktop = new DesktopIntegrationService(restartedHost, new(), measurements, restartedNative,
                recoveryStore: new JsonIconRecoveryStore(directory, NullLogger<JsonIconRecoveryStore>.Instance),
                settings: new SettingsViewModel(new ProbeStartup(), NullLogger<SettingsViewModel>.Instance, preferenceStore)))
            {
                var restarted = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance, restartedDesktop);
                restartedDesktop.Start(); await Task.Delay(300);
                await WaitPositions(restarted.Fences[0]); Capture("restart-collapsed");
                if (!restarted.Fences[0].IsCollapsed || restarted.Fences[0].Icons.Length != 10) throw new InvalidOperationException("Restart lost collapsed assignments.");
                if (restarted.Fences[0].Appearance != styledAppearance) throw new InvalidOperationException("Restart lost appearance settings.");
                var again = typeof(Win32DesktopHost).GetMethod("NotifyCollapse", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<Fence>>(restartedHost);
                again(restarted.Fences[0]); await WaitPositions(restarted.Fences[0].ToggleCollapsed());
                for (var wait = 0; restartedDesktop.IsPresentingTransition && wait < 300; wait++) await Task.Delay(1);
                if (restarted.Fences[0].IsCollapsed) throw new InvalidOperationException("Expand after restart did not commit.");
                Capture("restart-expanded"); Console.WriteLine("PASS: persisted collapsed Fence restored, all ten real icons parked, then expanded to saved layout");
            }
            if (DesktopDiagnostics.IconVisibility(handles) != originalPresentation || DesktopDiagnostics.IconRegion(handles) != originalRegion) throw new InvalidOperationException("Restart did not restore Explorer properties.");
            File.WriteAllText(Path.Combine(directory, "animation.json"), JsonSerializer.Serialize(animationReports, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(directory, "latency.json"), JsonSerializer.Serialize(new { collapse, expand, visualCollapse = measurements.Collapse, visualExpand = measurements.Expand, performance = measurements.Performance }));
            Console.WriteLine("Artifacts: " + directory);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Scenario failure before cleanup: " + error);
            throw;
        }
        finally
        {
            desktop.Dispose(); native.Dispose(); window.Close();
            // Explorer can displace neighbours when probes cross occupied cells. Restore the whole snapshot.
            var originalTargets = initial.Icons.Select(i => new DesktopIconTarget(i.StableId, i.Position)).ToArray();
            // A read taken during resize can intentionally omit unstable identities. Refresh the
            // complete Shell snapshot before restoring; never reuse that partial PIDL cache.
            DesktopIconSnapshot refreshed = await reader.ReadAsync();
            for (var retry = 0; (refreshed.Error is not null || refreshed.Icons.Count != initial.Icons.Count) && retry < 50; retry++)
            { await Task.Delay(10); refreshed = await reader.ReadAsync(); }
            if (refreshed.Error is not null || refreshed.Icons.Count != initial.Icons.Count)
                throw new InvalidOperationException("Cannot obtain a stable restoration snapshot; original-positions.json retained.");
            await reader.PositionAsync(originalTargets);
            reader.Dispose(); // Verify after the session restores the original Explorer grid flags.
            using var verifier = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
            var restored = await verifier.ReadAsync();
            for (var retry = 0; !IconPlacement.AtTargets(restored, originalTargets) && retry < 50; retry++)
            { await Task.Delay(10); restored = await verifier.ReadAsync(); }
            if (!IconPlacement.AtTargets(restored, originalTargets))
                throw new InvalidOperationException("Original positions not restored.");
            Console.WriteLine($"PASS: all {originalTargets.Length} original desktop item positions restored after grid restoration");
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
            if (!showDesktop) return; // Background API verification must not capture or activate other apps.
            var area = captureArea ?? new(550, 70, 1400, 1100);
            var pixels = DesktopDiagnostics.CaptureScreenArea(area);
            var bitmap = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgr32, null, pixels, area.Width * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
        }
    }
    private sealed class ProbeStartup : IStartupRegistration
    { public bool IsEnabled() => false; public void SetEnabled(bool enabled) { } }
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
            if (error is not null || message.Contains("latency", StringComparison.Ordinal) || message.Contains("performance", StringComparison.OrdinalIgnoreCase) || message.StartsWith("Fence animation", StringComparison.Ordinal))
                Console.WriteLine(message + (error is null ? "" : " " + error));
        }
    }
}
