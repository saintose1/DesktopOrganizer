using System.Diagnostics;
using System.IO;
using System.Reflection;
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

/// <summary>Real Explorer/API smoke scenario, not a substitute for real OLE/mouse verification.</summary>
internal static class DesktopSmoke
{
    public static int Run()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DesktopOrganizer.App;component/Themes/LavenderHud.xaml", UriKind.Relative) });
        var exit = 1;
        application.Dispatcher.InvokeAsync(async () =>
        {
            try { await Scenario(); exit = 0; }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { application.Shutdown(); }
        });
        application.Run();
        return exit;
    }

    private static async Task Scenario()
    {
        foreach (var process in Process.GetProcessesByName("DesktopOrganizer.App"))
            using (process) throw new InvalidOperationException("Exit production DesktopOrganizer through its tray before this isolated smoke.");
        var directory = Path.GetFullPath(Path.Combine(".tools", "icons-smoke", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(directory);
        var store = new JsonFenceStore(directory, NullLogger<JsonFenceStore>.Instance);
        using var native = Icons();
        var initial = await native.ReadAsync();
        Check(initial.Connected && initial.CanPosition && initial.Error is null, "Real icon reader connected");
        var probes = initial.Icons.Where(i => i.StableId.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)).Take(3).ToArray();
        Check(probes.Length == 3, "Three real Explorer shortcuts available");
        DesktopIntegrationService? desktop = null;
        DesktopIconService? icons = null;
        MainWindow? window = null;
        try
        {
            var host = Host(); icons = Icons();
            desktop = Desktop(host, icons, directory);
            var vm = ViewModel(store, desktop);
            window = new MainWindow(vm); window.Show();
            SetApplicationWindow(window);
            vm.CreateCommand.Execute(null);
            vm.X = "600"; vm.Y = "120"; vm.Width = "450"; vm.Height = "350"; vm.Name = "Explorer icons probe";
            vm.ApplyCommand.Execute(null);
            vm.CreateCommand.Execute(null);
            vm.X = "1100"; vm.Y = "120"; vm.Width = "400"; vm.Height = "350"; vm.Name = "Second probe";
            vm.ApplyCommand.Execute(null);
            desktop.Start();
            await Wait(() => desktop.Connection.IconStatus.StartsWith("Desktop icons: 52") || desktop.Connection.IconStatus.StartsWith($"Desktop icons: {initial.Icons.Count}"));
            await RevealDesktop(desktop, new(800, 300));
            var coordinator = (DesktopIconCoordinator)typeof(DesktopIntegrationService).GetField("_icons", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(desktop)!;
            ((System.Windows.Threading.DispatcherTimer)typeof(DesktopIconCoordinator).GetField("_timer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(coordinator)!).Stop();
            var fence = vm.Fences[0];
            await PositionThroughDropAdapter(coordinator, native, probes,
                probes.Select((i, index) => new DesktopIconTarget(i.StableId, new(fence.X + 100 + index * 100, fence.Y + 100))).ToArray(),
                new(fence.X + 100, fence.Y + 100));
            await Wait(() => vm.Fences[0].Icons.Length == 3);
            Check(true, "Captured-group adapter reconciled real Shell positions with polling disabled (API/adapter replay, not real mouse/OLE or selected-set capture)");
            Check(store.Load().Fences[0].Icons.Length == 3, "Post-move detection assigned/persisted three real icons");
            await CheckPositions(native, vm.Fences[0]);
            Console.WriteLine(DesktopDiagnostics.IconVisibility(host.Handles!));
            await RevealDesktop(desktop, new(800, 300)); await Task.Delay(100);
            Capture(host, directory, "assigned");
            Console.WriteLine($"Capture: {directory}");
            var body = DesktopDiagnostics.WindowAt(new(800, 300));
            var header = DesktopDiagnostics.WindowAt(new(750, 136));
            Console.WriteLine($"Body hit: {body}; Header hit: {header}");
            Check((body.Contains("SysListView32") || body.Contains("SHELLDLL_DefView")) && header.Contains("DesktopOrganizer.FenceSurface"), "Content input reaches Explorer; header reaches own HWND");

            var notify = typeof(Win32DesktopHost).GetMethod("NotifyInteraction", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action<FenceInteractionEventArgs>>(host);
            vm.SelectedFence = vm.Fences[0];
            var before = File.ReadAllText(store.FilePath);
            var original = vm.Fences[0];
            notify(new(original, InteractionPhase.Started) { Hit = FenceHit.Header });
            for (var i = 1; i <= 25; i++)
            {
                notify(new(original.WithBounds(original.X + i * 4, original.Y + i * 2, original.Width, original.Height), InteractionPhase.Preview) { Hit = FenceHit.Header });
                await Task.Delay(8);
            }
            Check(File.ReadAllText(store.FilePath) == before, "Gesture previews do not write JSON");
            notify(new(original, InteractionPhase.Completed) { Hit = FenceHit.Header });
            await Wait(() => store.Load().Fences[0].X == original.X + 100);
            Check(vm.Fences[0].X == original.X + 100 && vm.Fences[0].Y == original.Y + 50, "Native interaction pipeline committed movement");
            Check(vm.Name == vm.Fences[0].Name, "Real WPF selection binding preserved editor name after desktop preview");
            await CheckPositions(native, vm.Fences[0]);
            Check(store.Load().Fences[0].X == vm.Fences[0].X, "Final movement persisted");

            original = vm.Fences[0];
            notify(new(original, InteractionPhase.Started) { Hit = FenceHit.Right });
            var scale = host.GetScale(original);
            Console.WriteLine($"Actual desktop DPI scale: {scale:F2}");
            var minimumWidth = FenceIconLayout.MinimumWidth(initial.Metrics, scale);
            var resized = FenceIconLayout.ConstrainResize(original.WithBounds(original.X, original.Y, minimumWidth, original.Height), original, FenceHit.Right, initial.Metrics, scale);
            notify(new(resized, InteractionPhase.Preview) { Hit = FenceHit.Right });
            notify(new(resized, InteractionPhase.Completed) { Hit = FenceHit.Right });
            await Wait(() => vm.Fences[0].Width == minimumWidth && store.Load().Fences[0].Width == minimumWidth);
            Check(vm.Fences[0].Icons[2].OffsetY > vm.Fences[0].Icons[0].OffsetY, "Resize reflowed real icons into a second row");
            await CheckPositions(native, vm.Fences[0]);

            original = vm.Fences[0];
            var toggle = typeof(Win32DesktopHost).GetMethod("NotifyCollapse", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<Fence>>(host);
            toggle(original);
            await Wait(() => vm.Fences[0].IsCollapsed && !desktop.IsPresentingTransition);
            Check(vm.Fences[0].IsCollapsed && vm.Fences[0].Height == original.Height, "Collapse preserved normal height");
            await WaitPositions(native, vm.Fences[0], host);
            Check(new JsonIconRecoveryStore(directory, NullLogger<JsonIconRecoveryStore>.Instance).Load().Count == 3, "Recovery journal written before parking real icons");
            var parked = (await native.ReadAsync()).Icons.Where(i => probes.Any(p => p.StableId == i.StableId)).ToArray();
            foreach (var icon in parked) Check(!host.GetMonitorAreas().Any(a => icon.Bounds.X < a.X + a.Width && icon.Bounds.X + icon.Bounds.Width > a.X &&
                icon.Bounds.Y < a.Y + a.Height && icon.Bounds.Y + icon.Bounds.Height > a.Y), "Collapsed real icon is outside visible desktop");
            Check(DesktopDiagnostics.WindowAt(new(vm.Fences[0].X + 50, vm.Fences[0].Y + FenceInteractionGeometry.HeaderHeight * scale - 4))
                .Contains("DesktopOrganizer.FenceSurface"), "Collapsed header retains its full input region");
            Capture(host, directory, "collapsed");
            await desktop.PrepareExitAsync();
            Check(IconPlacement.AtTargets(await native.ReadAsync(), IconPlacement.ReturnPositions(vm.Fences.ToArray())),
                "Graceful exit returns collapsed real icons to desktop before restoring Explorer properties");
            Check(store.Load().Fences[0].IsCollapsed && store.Load().Fences[0].Icons.Length == 3,
                "Graceful exit preserves persisted collapse and assignments");
            Check(new JsonIconRecoveryStore(directory, NullLogger<JsonIconRecoveryStore>.Instance).Load().Count == 0,
                "Return journal cleared only after verified icon restoration");
            desktop.Dispose(); desktop = null; icons.Dispose(); icons = null; window.Close(); window = null;
            native.Dispose(); // Restore grid before reconnect to exercise first-position folder-flag changes.
            using var restartedReader = Icons();
            host = Host(); icons = Icons(); desktop = Desktop(host, icons, directory);
            vm = ViewModel(store, desktop); window = new MainWindow(vm); window.Show(); SetApplicationWindow(window);
            desktop.Start();
            await RevealDesktop(desktop, new(800, 300));
            await Wait(() => desktop.Connection.IconStatus.StartsWith($"Desktop icons: {initial.Icons.Count}"));
            await WaitPositions(restartedReader, vm.Fences[0], host);
            Check(vm.Fences[0].Icons.Length == 3 && vm.Fences[0].Width == minimumWidth && vm.Fences[0].IsCollapsed, "Fresh desktop service restored collapsed state; real icons stayed parked");
            notify = typeof(Win32DesktopHost).GetMethod("NotifyInteraction", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action<FenceInteractionEventArgs>>(host);
            original = vm.Fences[0];
            toggle = typeof(Win32DesktopHost).GetMethod("NotifyCollapse", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<Fence>>(host);
            toggle(original);
            await Wait(() => !vm.Fences[0].IsCollapsed && !desktop.IsPresentingTransition);
            await WaitPositions(restartedReader, vm.Fences[0], host);
            Check(new JsonIconRecoveryStore(directory, NullLogger<JsonIconRecoveryStore>.Instance).Load().Count == 3, "Expand restored relative positions; return journal remains armed outside the next collapse critical path");

            var headerAfterRestore = DesktopDiagnostics.WindowAt(new(vm.Fences[0].X + 50, vm.Fences[0].Y + 12));
            Console.WriteLine($"Header after cold icon restore: {headerAfterRestore}");
            Check(headerAfterRestore.Contains("DesktopOrganizer.FenceSurface"), "Header remains interactive after Shell restores icon positions");
            coordinator = (DesktopIconCoordinator)typeof(DesktopIntegrationService).GetField("_icons", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(desktop)!;
            var outgoing = (await restartedReader.ReadAsync()).Icons.Single(i => i.StableId == probes[0].StableId);
            await PositionThroughDropAdapter(coordinator, restartedReader, [outgoing], [new(outgoing.StableId, new(1800, 700))], new(1800, 700));
            await Wait(() => vm.Fences[0].Icons.Length == 2);
            var afterOut = (await restartedReader.ReadAsync()).Icons.Single(i => i.StableId == probes[0].StableId);
            Check(afterOut.Position == new DesktopPoint(1800, 700), "Unassigned real icon remains at new desktop position");
            Check(store.Load().Fences[0].Icons.Length == 2, "Drop-out assignment removal persisted");
            Console.WriteLine($"Smoke artifacts: {directory}");
        }
        finally
        {
            desktop?.Dispose(); icons?.Dispose(); window?.Close();
            using var restoreReader = Icons();
            await restoreReader.ReadAsync();
            var originalTargets = initial.Icons.Select(i => new DesktopIconTarget(i.StableId, i.Position)).ToArray();
            await restoreReader.PositionAsync(originalTargets);
            restoreReader.Dispose();
            using var verifier = Icons();
            Check(IconPlacement.AtTargets(await verifier.ReadAsync(), originalTargets),
                $"All {originalTargets.Length} original desktop item positions restored after grid restoration");
        }
    }
    private static void SetApplicationWindow(Window window) { Application.Current.MainWindow = window; }
    private static async Task PositionThroughDropAdapter(DesktopIconCoordinator coordinator, DesktopIconService native,
        IReadOnlyList<DesktopIcon> source, IReadOnlyList<DesktopIconTarget> targets, DesktopPoint release)
    {
        // Contract/API replay with actual item identities, not real selection, input or OLE.
        // Programmatic Shell motion alone must no longer infer individual item assignments.
        var notify = typeof(DesktopIconCoordinator).GetMethod("OnExplorerPointer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<Action<ExplorerPointerEvent>>(coordinator);
        var selection = new DesktopSelection(source.Select(i => i.StableId).ToArray(), source[0].StableId)
            { InitialPositions = source.Select(i => new DesktopIconTarget(i.StableId, i.Position)).ToArray() };
        notify(new(ExplorerPointerPhase.Pressed, source[0].Position, false, true) { SelectionSnapshot = Task.FromResult(selection) });
        notify(new(ExplorerPointerPhase.DragStarted, source[0].Position, false, true));
        await native.PositionAsync(targets);
        notify(new(ExplorerPointerPhase.Released, release, false, true));
    }
    private static DesktopIconService Icons() => new(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
    private static Win32DesktopHost Host() => new(new DesktopDiscovery(NullLogger<DesktopDiscovery>.Instance), NullLogger<Win32DesktopHost>.Instance);
    private static DesktopIntegrationService Desktop(Win32DesktopHost host, DesktopIconService icons, string directory) => new(host, new FenceBitmapRenderer(), NullLogger<DesktopIntegrationService>.Instance, icons,
        recoveryStore: new JsonIconRecoveryStore(directory, NullLogger<JsonIconRecoveryStore>.Instance));
    private static MainWindowViewModel ViewModel(JsonFenceStore store, DesktopIntegrationService desktop) => new(store, NullLogger<MainWindowViewModel>.Instance, desktop);
    private static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
    private static async Task Wait(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready()) { if (timer.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Real Explorer smoke condition did not complete."); await Task.Delay(100); }
    }
    private static async Task RevealDesktop(DesktopIntegrationService desktop, DesktopPoint body)
    {
        // Wait for real desktop visibility rather than mistaking foreground occlusion for input blocking.
        for (var attempt = 0; attempt < 2 && !DesktopDiagnostics.WindowAt(body).Contains("SysListView32"); attempt++)
        {
            desktop.ShowDesktop(); await Task.Delay(200);
        }
    }
    private static async Task CheckPositions(DesktopIconService native, Fence fence)
    {
        var snapshot = await native.ReadAsync();
        foreach (var assignment in fence.Icons)
        {
            var icon = snapshot.Icons.Single(i => i.StableId == assignment.StableId);
            Check(Math.Abs(icon.Position.X - fence.X - assignment.OffsetX) + Math.Abs(icon.Position.Y - fence.Y - assignment.OffsetY) <= 2,
                "Real Explorer icon follows Fence/grid position");
        }
    }
    private static async Task WaitPositions(DesktopIconService native, Fence fence, Win32DesktopHost host)
    {
        var targets = IconPlacement.Targets([fence], host.GetMonitorAreas(), (await native.ReadAsync()).Metrics);
        for (var i = 0; i < 50; i++)
        {
            if (IconPlacement.AtTargets(await native.ReadAsync(), targets)) { Console.WriteLine("PASS: verified actual " + (fence.IsCollapsed ? "offscreen" : "expanded") + " positions"); return; }
            await Task.Delay(16);
        }
        throw new InvalidOperationException("Real icon positioning did not complete.");
    }
    private static void Capture(Win32DesktopHost host, string directory, string label)
    {
        host.FlushPainting();
        var surface = DesktopDiagnostics.Inspect(host.Handles!).Single(s => s.Caption.EndsWith("Explorer icons probe", StringComparison.Ordinal));
        var area = surface.Bounds;
        var pixels = DesktopDiagnostics.CaptureScreenArea(area);
        var bitmap = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgr32, null, pixels, area.Width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, label + ".png")); encoder.Save(file);
    }
}
