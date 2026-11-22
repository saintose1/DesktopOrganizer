using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopOrganizer.App.Desktop;
using DesktopOrganizer.App.Themes;
using DesktopOrganizer.App.ViewModels;
using DesktopOrganizer.App.Views;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Infrastructure.Interop;
using DesktopOrganizer.Infrastructure;
using DesktopOrganizer.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Real Shell/file-notification checks with owned fixtures. Gesture replay is not physical mouse/OLE verification.</summary>
internal static class ExplorerStabilitySmoke
{
    private static readonly object LogGate = new();
    public static int Run()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var result = 1;
        application.Dispatcher.InvokeAsync(async () =>
        {
            try { await Scenario(); result = 0; }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { application.Shutdown(); }
        });
        application.Run(); return result;
    }
    private static async Task Scenario()
    {
        foreach (var process in Process.GetProcessesByName("DesktopOrganizer.App"))
            using (process) throw new InvalidOperationException("Exit production DesktopOrganizer through its tray before this isolated smoke.");
        var directory = Path.GetFullPath(Path.Combine(".tools", "explorer-stability-smoke", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(directory);
        var diagnosticsPath = Path.Combine(directory, "diagnostics.log");
        var prefix = "DesktopOrganizer-probe-" + Guid.NewGuid().ToString("N") + "-";
        var desktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(desktopDirectory)) throw new InvalidOperationException("Current-user desktop directory is unavailable.");
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trace = new ExplorerInteractionTrace(new SmokeLogger<ExplorerInteractionTrace>(diagnosticsPath));
        using var native = Icons(diagnosticsPath, trace);
        var initial = await Healthy(native);
        var originals = initial.Icons.Select(icon => new DesktopIconTarget(icon.StableId, icon.Position)).ToArray();
        File.WriteAllText(Path.Combine(directory, "original-positions.json"), JsonSerializer.Serialize(originals));
        var host = new Win32DesktopHost(new(new SmokeLogger<DesktopDiscovery>(diagnosticsPath)), new SmokeLogger<Win32DesktopHost>(diagnosticsPath),
            Path.Combine(directory, "desktop-presentation.json"));
        DesktopIntegrationService? desktop = null;
        IReadOnlyList<Fence> fences = [];
        var store = new JsonFenceStore(directory, NullLogger<JsonFenceStore>.Instance);
        var removed = new HashSet<string>(FenceIconLayout.IdentityComparer);
        var renamed = new List<(string Before, string After)>();
        host.IconRemoved += identity => removed.Add(identity);
        host.IconRenamed += (before, after) => renamed.Add((before, after));
        double? wallpaperBaseline = null;
        var wallpaperSamples = new Dictionary<string, double>();
        DesktopHandles? observedHandles = null;
        string? parentPresentation = null;
        DesktopArea sample = default;
        Exception? scenarioFailure = null;
        var canConcealFixtures = false;
        try
        {
            var paths = Enumerable.Range(0, 3).Select(CreateOwned).ToArray();
            var snapshot = await Healthy(native, current => paths.All(path => Find(current, path) is not null));
            var animationIds = paths.Take(2).ToHashSet(FenceIconLayout.IdentityComparer);
            trace.Begin();
            var animationItems = await native.ReadAnimationItemsAsync(animationIds);
            Check(animationItems.Error is null && animationItems.Icons.SequenceEqual(snapshot.Icons.Where(i => animationIds.Contains(i.StableId))) &&
                animationItems.Metrics == snapshot.Metrics && trace.Snapshot.Any(p => p.Stage == "Animation.snapshot.scoped" && p.Count == 2),
                "Scoped animation read refreshed exactly two real positions and glyph bounds, preserving desktop metrics");
            trace.Complete("scoped animation inventory");
            trace.Begin();
            await native.ReconcileDropAsync(_ => []);
            Check(trace.Snapshot.Any(p => p.Stage == "Drop.positions.cached"), "Stable drop refreshed actual positions using retained Shell identities");
            trace.Complete("stable cached drop / no positioning");
            var cachePath = CreateOwned(4);
            using (var verifier = Icons(diagnosticsPath))
            {
                await Healthy(verifier, current => Find(current, cachePath) is not null);
                trace.Begin();
                await native.ReconcileDropAsync(current =>
                { Check(Find(current, cachePath) is not null, "New identity cannot be omitted from a drop snapshot"); return []; });
                Check(!trace.Snapshot.Any(p => p.Stage == "Drop.positions.cached"), "Changed inventory forced full enumeration before drop planning");
                trace.Complete("inventory fallback / no positioning");
                DeleteOwned(cachePath);
                await Healthy(verifier, current => Find(current, cachePath) is null);
                trace.Begin();
                var refreshedItems = await native.ReadAnimationItemsAsync(animationIds);
                Check(refreshedItems.Error is null && refreshedItems.Icons.Count == 2 &&
                    trace.Snapshot.Any(p => p.Stage == "Animation.snapshot.fullFallback"),
                    "Animation snapshot detected changed desktop inventory and used the full validation fallback");
                trace.Complete("animation inventory fallback");
                await native.ReconcileDropAsync(current =>
                { Check(Find(current, cachePath) is null, "Deleted identity cannot survive a drop cache refresh"); return []; });
            }
            var start = animationItems.Icons.Select(icon => new DesktopIconTarget(icon.StableId, icon.Position)).ToArray();
            var moved = start.Select(target => target with { Position = target.Position with { Y = target.Position.Y + 12 } }).ToArray();
            await using (var animation = await native.BeginAnimationAsync(start, moved))
            {
                await animation.PositionAsync([moved[1], moved[0]]);
                await animation.PositionAsync([start[0]]);
                await animation.CompleteFrameAsync();
                var after = await Healthy(native);
                Check(IconPlacement.AtTargets(after, [start[0], moved[1]]),
                    "A one-item frame after a two-item frame only positions the supplied Shell identity");
                await animation.PositionAsync(start);
            }
            using (var revealer = new DesktopIconService(new SmokeLogger<DesktopIconService>(diagnosticsPath),
                new SmokeLogger<DesktopDiscovery>(diagnosticsPath), trace, Path.Combine(directory, HiddenItemsRecovery.JournalName)))
            {
                if ((await revealer.ConcealAsync([start[0].StableId])).Count != 0)
                {
                    canConcealFixtures = true;
                    await Healthy(native, current => Find(current, paths[0]) is null);
                    trace.Begin();
                    var redraws = revealer.PositionCounters.RedrawCompletions;
                    await revealer.RevealAsync([(start[0].StableId, start[0].Position)]);
                    Check(IconPlacement.AtTargets(await Healthy(native), [start[0]]),
                        "Reveal placed a hidden fixture at its target before returning");
                    Check(trace.Snapshot.Any(p => p.Stage == "Reveal.inventoryReads" && p.Count > 0) &&
                        revealer.PositionCounters.RedrawCompletions == redraws + 1,
                        "Reveal confirmed Shell membership and completed redraw once");
                    trace.Complete("hidden fixture reveal");
                }
            }
            Check(host.Connect(), "Validated real Explorer host available");
            observedHandles = host.Handles;
            parentPresentation = ParentPresentation();
            var area = host.GetArea();
            sample = new(area.X, area.Y + area.Height * 3 / 4, area.Width, Math.Min(100, area.Height / 8));
            DesktopShell.ShowDesktop(); await Task.Delay(250);
            CompositionState("before-surfaces");
            wallpaperBaseline = await Capture("before-surfaces");
            var first = new Fence(Guid.NewGuid(), "Stability probe", area.X + area.Width / 3, area.Y + 80, Math.Min(620, area.Width / 3), 350,
                icons: paths.Select(path => Find(snapshot, path)!).Select(icon => new DesktopIconAssignment(icon.StableId, icon.DisplayName, 0, 0)));
            first = FenceIconLayout.Arrange(first, snapshot.Metrics, host.GetScale(first));
            var second = new Fence(Guid.NewGuid(), "Collapsed stability probe", area.X + area.Width * 2 / 3, area.Y + 80,
                Math.Min(350, area.Width / 4), 350, true);
            fences = [first, second]; store.Save(fences.ToArray());
            desktop = new(host, new FenceBitmapRenderer(), new SmokeLogger<DesktopIntegrationService>(diagnosticsPath), native,
                new SmokeLogger<DesktopIconCoordinator>(diagnosticsPath),
                recoveryStore: new JsonIconRecoveryStore(directory, NullLogger<JsonIconRecoveryStore>.Instance));
            desktop.StateChangeRequested += (_, request) =>
            {
                store.Save(request.Fences.ToArray()); fences = request.Fences.ToArray();
                desktop.SetFences(fences); request.Accepted = true;
            };
            desktop.SetFences(fences); desktop.Start();
            var coordinator = (DesktopIconCoordinator)typeof(DesktopIntegrationService).GetField("_icons", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(desktop)!;
            await Positions(); CompositionState("after-surfaces"); await Capture("after-surfaces");

            var ordinaryPath = CreateOwned(3);
            await Healthy(native, current => Find(current, ordinaryPath) is not null);
            await Task.Delay(350); CompositionState("after-new-desktop-item"); await Capture("after-new-desktop-item");
            Check(fences[0].Icons.Length == 3 && fences[1].Icons.IsEmpty, "New desktop item does not change existing assignments");

            var deletedId = Find(snapshot, paths[1])!.StableId;
            DeleteOwned(paths[1]);
            await Wait(() => removed.Contains(deletedId) && fences[0].Icons.Length == 2);
            await Positions();
            Check(FenceIconLayout.IdentityComparer.Equals(fences[0].Icons[1].StableId, Find(snapshot, paths[2])!.StableId),
                "Actual Shell DELETE notification removed only the deleted middle assignment");
            var compact = FenceIconLayout.GridOffset(1, fences[0].Width, coordinator.Metrics, host.GetScale(fences[0]));
            Check(fences[0].Icons[1].OffsetX == compact.X && fences[0].Icons[1].OffsetY == compact.Y,
                "Remaining real Explorer item filled the middle grid slot and persisted");
            Check(store.Load().Fences[0].Icons.Length == 2, "Compacted membership persisted to isolated profile");

            var renamedPath = Path.Combine(desktopDirectory, prefix + "renamed.txt");
            File.Move(paths[0], renamedPath); owned.Remove(paths[0]); owned.Add(renamedPath);
            DesktopDiagnostics.NotifySmokeItemChange(paths[0], renamedPath);
            snapshot = await Healthy(native, current => Find(current, renamedPath) is not null);
            var renamedId = Find(snapshot, renamedPath)!.StableId;
            await Wait(() => renamed.Any(pair => FenceIconLayout.IdentityComparer.Equals(pair.After, renamedId)) &&
                fences[0].Icons.Any(icon => FenceIconLayout.IdentityComparer.Equals(icon.StableId, renamedId)));
            Check(fences[0].Icons.Length == 2, "Actual Shell rename pair preserved assignment and order");

            // The commit callback precedes transaction completion; do not inject the next fixture state into that transaction.
            var applying = typeof(DesktopIconCoordinator).GetField("_applyingDrop", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await Wait(() => !(bool)applying.GetValue(coordinator)!);

            // Split only owned fixtures so ordinary motion can exercise expanded and parked assignments together.
            fences = [fences[0].WithIcons([fences[0].Icons[0]]),
                FenceIconLayout.Arrange(fences[1].WithIcons([fences[0].Icons[1]]), coordinator.Metrics, host.GetScale(fences[1]))];
            store.Save(fences.ToArray()); desktop.SetFences(fences); await Positions();

            var toggle = typeof(Win32DesktopHost).GetMethod("NotifyCollapse", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action<Fence>>(host);
            var neighbor = Find(await Healthy(native), paths[2])!;
            foreach (var collapsed in new[] { true, false })
            {
                trace.Begin();
                toggle(fences[0]);
                await Wait(() => !desktop.IsPresentingTransition && fences[0].IsCollapsed == collapsed);
                var writes = trace.Snapshot.Where(p => p.Stage == "Animation.SelectAndPositionItems.begin").ToArray();
                Check(writes.Length > 0 && writes.All(p => p.Count == 1), "Animated Shell batches position only the transitioning Fence item");
                Check(trace.Snapshot.Count(p => p.Stage == "Animation.snapshot.scoped" && p.Count == 1) == 2,
                    "Transition preparation and final verification both read just one real Explorer item");
                Check(!trace.Snapshot.Any(p => p.Stage == "Animation.snapshot.fullFallback" || p.Stage == "Verify.correct"),
                    "Verified transition needs neither a full desktop read nor a corrective Shell move");
                trace.Complete(collapsed ? "isolated collapse" : "isolated expand");
                var presented = await Healthy(native);
                Check(Find(presented, paths[2])?.Position == neighbor.Position, "Neighbor's real Explorer icon kept its parked coordinates throughout the transition");
                Check(IconPlacement.AtTargets(presented, IconPlacement.Targets(fences, host.GetMonitorAreas(), presented.Metrics)),
                    "Both Fences retained their expected real Explorer positions");
            }
            var before = fences.ToArray();
            var expected = IconPlacement.Targets(before, host.GetMonitorAreas(), coordinator.Metrics);
            var persisted = File.ReadAllText(store.FilePath);
            snapshot = await Healthy(native);
            var ordinary = Find(snapshot, ordinaryPath)!;
            var dropPoint = new DesktopPoint(area.X + area.Width - 180, area.Y + area.Height - 200);
            var counters = native.PositionCounters;
            var notify = typeof(DesktopIconCoordinator).GetMethod("OnExplorerPointer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action<ExplorerPointerEvent>>(coordinator);
            var selection = new DesktopSelection([ordinary.StableId], ordinary.StableId)
                { InitialPositions = [new(ordinary.StableId, ordinary.Position)] };
            notify(new(ExplorerPointerPhase.Pressed, ordinary.Position, false, true) { SelectionSnapshot = Task.FromResult(selection) });
            notify(new(ExplorerPointerPhase.DragStarted, ordinary.Position, false, true));
            await native.PositionAsync([new(ordinary.StableId, dropPoint)]);
            notify(new(ExplorerPointerPhase.Released, dropPoint, false, true));
            await Wait(() => coordinator.ExplorerPhase == ExplorerGesturePhase.Idle);
            await Task.Delay(350);
            snapshot = await Healthy(native);
            Check(IconPlacement.AtTargets(snapshot, expected), "Ordinary icon adapter replay preserved expanded and collapsed assigned positions");
            Check(before.SequenceEqual(fences), "Ordinary icon adapter replay preserved all Fence assignments");
            Check(native.PositionCounters.Batches - counters.Batches == 1,
                "Ordinary move issued exactly its own Shell position batch; zero unrelated Fence reposition batches");
            Check(File.ReadAllText(store.FilePath) == persisted, "Ordinary desktop move did not write Fence JSON");
            // Replay the actual captured failure: Explorer's OLE drop also ejects an untouched parked item.
            var parkedId = fences[1].Icons[0].StableId;
            var secondDrop = new DesktopPoint(dropPoint.X - 150, dropPoint.Y);
            counters = native.PositionCounters;
            selection = new DesktopSelection([ordinary.StableId], ordinary.StableId)
                { InitialPositions = [new(ordinary.StableId, dropPoint)] };
            trace.Begin();
            notify(new(ExplorerPointerPhase.Pressed, dropPoint, false, true) { SelectionSnapshot = Task.FromResult(selection) });
            notify(new(ExplorerPointerPhase.DragStarted, dropPoint, false, true));
            await native.PositionAsync([new(ordinary.StableId, secondDrop), new(parkedId, new(area.X + 50, area.Y + 50))]);
            notify(new(ExplorerPointerPhase.Released, secondDrop, false, true));
            await Wait(() => coordinator.ExplorerPhase == ExplorerGesturePhase.Idle);
            snapshot = await Healthy(native);
            Check(IconPlacement.AtTargets(snapshot, expected), "Observed OLE-reflow adapter restored only ejected collapsed parking; expanded group unchanged");
            Check(Find(snapshot, ordinaryPath)!.Position == secondDrop, "Ordinary dragged item retained its user's drop point during parking restoration");
            Check(native.PositionCounters.Batches - counters.Batches == 2,
                "One simulated Explorer batch plus one parking repair batch; no general Fence relayout");
            Check(trace.Snapshot.Any(point => point.Stage == "Drop.positions.scopedParking" && point.Count == 1),
                "Ordinary drop reads only the collapsed group's position before its visual repair");
            trace.Complete("ordinary drop / scoped parking repair");
            Check(before.SequenceEqual(fences) && File.ReadAllText(store.FilePath) == persisted,
                "Parking repair preserved assignments and did not write Fence JSON");
            await Capture("after-ordinary-move");
            await desktop.PrepareExitAsync(); desktop.Dispose(); desktop = null;
            await Capture("after-graceful-exit"); CompositionState("after-graceful-exit");
            if (canConcealFixtures) await CheckConcealedGroupsAsync(fences[0], fences[1], ordinaryPath);
            else Console.WriteLine("SKIP: Explorer displays hidden test fixtures.");
            Console.WriteLine("PASS: Shell/API smoke completed. This was not physical OLE, double-click, marquee, uninstall, hover or animation verification.");
            Console.WriteLine("Smoke evidence: " + directory);
        }
        catch (Exception error)
        {
            scenarioFailure = error;
            try { File.AppendAllText(diagnosticsPath, "SCENARIO FAILURE: " + error + Environment.NewLine); }
            catch (IOException loggingError) { Console.Error.WriteLine("Diagnostic write warning: " + loggingError.Message); }
            throw;
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            async Task Attempt(Func<Task> action)
            {
                try { await action(); } catch (Exception error) { cleanupErrors.Add(error); }
            }
            if (desktop is not null) await Attempt(desktop.PrepareExitAsync);
            await Attempt(() => { desktop?.Dispose(); return Task.CompletedTask; });
            await Attempt(() => { host.Dispose(); return Task.CompletedTask; });
            foreach (var path in owned.ToArray()) await Attempt(() => { DeleteOwned(path); return Task.CompletedTask; });
            await Attempt(async () => { await Healthy(native, current => !current.Icons.Any(icon => icon.StableId.Contains(prefix, StringComparison.Ordinal))); });
            // A delayed Shell deletion notification must never prevent restoring unrelated original item positions.
            await Attempt(() => native.PositionAsync(originals));
            await Attempt(() => { native.Dispose(); return Task.CompletedTask; });
            await Attempt(async () =>
            {
                using var verifier = Icons(diagnosticsPath);
                Check(IconPlacement.AtTargets(await Healthy(verifier), originals), "All original user item positions restored after Shell grid cleanup");
            });
            await Attempt(() =>
            {
                File.WriteAllText(Path.Combine(directory, "metrics.json"), JsonSerializer.Serialize(new
                { RemovedEvents = removed.Count, RenameEvents = renamed.Count, WallpaperSamples = wallpaperSamples,
                    ScenarioError = scenarioFailure?.Message, CleanupErrors = cleanupErrors.Select(error => error.Message).ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
                return Task.CompletedTask;
            });
            foreach (var error in cleanupErrors) Console.Error.WriteLine("CLEANUP WARNING: " + error.Message);
            if (scenarioFailure is null && cleanupErrors.Count > 0) throw new AggregateException("Smoke cleanup was incomplete.", cleanupErrors);
        }
        async Task CheckConcealedGroupsAsync(Fence first, Fence second, string ordinaryPath)
        {
            var protectionDirectory = Path.Combine(directory, "concealed");
            Directory.CreateDirectory(protectionDirectory);
            using var workspace = new DesktopIconService(new SmokeLogger<DesktopIconService>(diagnosticsPath),
                new SmokeLogger<DesktopDiscovery>(diagnosticsPath), trace, Path.Combine(protectionDirectory, HiddenItemsRecovery.JournalName));
            using var protectionHost = new Win32DesktopHost(new(new SmokeLogger<DesktopDiscovery>(diagnosticsPath)),
                new SmokeLogger<Win32DesktopHost>(diagnosticsPath), Path.Combine(protectionDirectory, "desktop-presentation.json"));
            using var protection = new DesktopIntegrationService(protectionHost, new FenceBitmapRenderer(),
                new SmokeLogger<DesktopIntegrationService>(diagnosticsPath), workspace,
                new SmokeLogger<DesktopIconCoordinator>(diagnosticsPath),
                recoveryStore: new JsonIconRecoveryStore(protectionDirectory, NullLogger<JsonIconRecoveryStore>.Instance));
            var search = new Fence(Guid.NewGuid(), "Search probe", second.X, second.Y + 140, 300, 200, isSearch: true);
            var groups = new[] { first.ToggleCollapsed(), second, search };
            protection.StateChangeRequested += (_, request) =>
            {
                groups = request.Fences.ToArray();
                protection.SetFences(groups);
                request.Accepted = true;
            };
            var hidden = groups.SelectMany(f => f.Icons).Select(icon => icon.StableId).ToHashSet(FenceIconLayout.IdentityComparer);
            var coordinator = (DesktopIconCoordinator)typeof(DesktopIntegrationService)
                .GetField("_icons", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(protection)!;
            var notify = typeof(DesktopIconCoordinator).GetMethod("OnExplorerPointer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action<ExplorerPointerEvent>>(coordinator);
            protection.SetFences(groups);
            protection.Start();
            try
            {
                DesktopPoint? searchAnchor = null;
                protection.SearchRequested += anchor => searchAnchor = anchor;
                var animate = typeof(Win32DesktopHost).GetMethod("NotifyCollapse", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .CreateDelegate<Action<Fence>>(protectionHost);
                animate(search);
                Check(searchAnchor is not null && !protection.IsPresentingTransition && groups[2].IsCollapsed,
                    "The search Fence's native header action requests the search popup without expanding a group");
                await Healthy(workspace, current => hidden.IsSubsetOf(workspace.Concealed) &&
                    !current.Icons.Any(icon => hidden.Contains(icon.StableId)));
                for (var drag = 0; drag < 2; drag++)
                {
                    var ordinary = Find(await Healthy(workspace), ordinaryPath)!;
                    var target = new DesktopPoint(ordinary.Position.X + (drag == 0 ? -40 : 40), ordinary.Position.Y);
                    var selected = new DesktopSelection([ordinary.StableId], ordinary.StableId)
                        { InitialPositions = [new(ordinary.StableId, ordinary.Position)] };
                    notify(new(ExplorerPointerPhase.Pressed, ordinary.Position, false, true) { SelectionSnapshot = Task.FromResult(selected) });
                    notify(new(ExplorerPointerPhase.DragStarted, ordinary.Position, false, true));
                    var batches = workspace.PositionCounters.Batches;
                    await workspace.PositionAsync([new(ordinary.StableId, target)]);
                    notify(new(ExplorerPointerPhase.Released, target, false, true));
                    await Wait(() => coordinator.ExplorerPhase == ExplorerGesturePhase.Idle);
                    if (drag == 0) await Task.Delay(2800); // Cross the old quiet-return threshold including the Shell update.
                    var after = await Healthy(workspace);
                    Check(hidden.IsSubsetOf(workspace.Concealed) && !after.Icons.Any(icon => hidden.Contains(icon.StableId)),
                        "All writable collapsed fixtures stayed absent before the next ordinary drag");
                    Check(workspace.PositionCounters.Batches == batches + 1, "Only the ordinary item was positioned");
                }
                var frames = 0;
                coordinator.FramePresented += (_, _) => frames++;
                animate(groups[0]);
                await Wait(() => !protection.IsPresentingTransition && !groups[0].IsCollapsed);
                Check(frames >= 2 && workspace.Concealed.Count == groups[1].Icons.Length,
                    "A requested expand restores and animates only the selected group's icons");
                Check(IconPlacement.AtTargets(await Healthy(workspace),
                    IconPlacement.Targets(groups, protectionHost.GetMonitorAreas(), coordinator.Metrics, groups[0].Id)),
                    "Expanded group retained its final positions");
                var catalog = new ProgramShortcutSearch();
                var entry = catalog.Load().FirstOrDefault() ?? throw new InvalidOperationException("No Windows shortcut available to test search.");
                ThemeManager.Apply("dark");
                Application.Current.Resources.MergedDictionaries.Add(new() { Source =
                    new Uri("/DesktopOrganizer.App;component/Themes/ForestControls.xaml", UriKind.Relative) });
                var searchModel = new ProgramSearchViewModel(catalog) { Query = entry.Name };
                var popup = new ProgramSearchWindow(searchModel, searchAnchor!.Value);
                try
                {
                    popup.Show();
                    await Wait(() => searchModel.Results.Any(result => result.Name == entry.Name));
                    Check(popup.IsVisible && popup.FindName("SearchBox") is System.Windows.Controls.TextBox &&
                        searchModel.OpenCommand.CanExecute(null), "The real WPF search popup shows matching Windows shortcuts");
                }
                finally { if (popup.IsVisible) popup.Close(); }
            }
            finally { await protection.PrepareExitAsync(); }
        }
        string CreateOwned(int index)
        {
            var path = Path.Combine(desktopDirectory, prefix + index + ".txt");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                owned.Add(path);
                using var writer = new StreamWriter(stream); writer.Write("DesktopOrganizer isolated smoke fixture.");
            }
            DesktopDiagnostics.NotifySmokeItemChange(path);
            return path;
        }
        void DeleteOwned(string path)
        {
            if (!owned.Contains(path) || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(desktopDirectory)) ||
                !Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove an item not created by this smoke.");
            if (File.Exists(path)) File.Delete(path);
            DesktopDiagnostics.NotifySmokeItemChange(path, deleted: true);
        }
        async Task Positions() => await Healthy(native, current => IconPlacement.AtTargets(current,
            IconPlacement.Targets(fences, host.GetMonitorAreas(), current.Metrics)));
        async Task<double?> Capture(string label)
        {
            Check(ParentPresentation() == parentPresentation, label + " preserved Explorer parent layered attributes");
            // Shell positions are synchronous, desktop composition is not. Sample a presented frame.
            var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (new DesktopCompositionClock().Subscribe(_ => presented.TrySetResult(), error => presented.TrySetException(error)))
                await presented.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var hit = DesktopDiagnostics.WindowAt(new(sample.X + sample.Width / 2, sample.Y + sample.Height / 2));
            if (!hit.Contains("SysListView32") && !hit.Contains("SHELLDLL_DefView"))
            { Console.WriteLine("SKIP: " + label + " wallpaper sample is covered by another application."); return null; }
            var pixels = DesktopDiagnostics.CaptureScreenArea(sample);
            var bitmap = BitmapSource.Create(sample.Width, sample.Height, 96, 96, PixelFormats.Bgr32, null, pixels, sample.Width * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(directory, label + ".png"))) encoder.Save(file);
            var black = 0;
            for (var pixel = 0; pixel < pixels.Length; pixel += 4)
                if (pixels[pixel] < 16 && pixels[pixel + 1] < 16 && pixels[pixel + 2] < 16) black++;
            var ratio = (double)black / (sample.Width * sample.Height);
            wallpaperSamples[label] = ratio;
            Console.WriteLine($"Wallpaper sample {label}: dark pixels={ratio:P1}");
            if (wallpaperBaseline is < 0.5) Check(ratio < 0.9, label + " did not blacken the sampled wallpaper region");
            return ratio;
        }
        string ParentPresentation() => DesktopDiagnostics.WallpaperWindowTree(observedHandles!)
            .Single(line => line.Contains("class=SHELLDLL_DefView;", StringComparison.Ordinal));
        void CompositionState(string label)
        {
            if (observedHandles is null || !new DesktopDiscovery(NullLogger<DesktopDiscovery>.Instance).Validate(observedHandles)) return;
            var state = label + ": " + DesktopDiagnostics.IconVisibility(observedHandles);
            Console.WriteLine(state);
            lock (LogGate) File.AppendAllText(diagnosticsPath, state + Environment.NewLine);
        }
    }
    private static DesktopIcon? Find(DesktopIconSnapshot snapshot, string path) => snapshot.Icons.SingleOrDefault(icon =>
        FenceIconLayout.IdentityComparer.Equals(icon.StableId, path));
    private static DesktopIconService Icons(string diagnosticsPath, ExplorerInteractionTrace? trace = null) =>
        new(new SmokeLogger<DesktopIconService>(diagnosticsPath), new SmokeLogger<DesktopDiscovery>(diagnosticsPath), trace);
    private static async Task<DesktopIconSnapshot> Healthy(DesktopIconService icons, Func<DesktopIconSnapshot, bool>? ready = null)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(12))
        {
            var snapshot = await icons.ReadAsync();
            if (snapshot.Connected && snapshot.CanPosition && snapshot.Error is null && (ready is null || ready(snapshot))) return snapshot;
            await Task.Delay(100);
        }
        throw new TimeoutException("Real Explorer did not publish a complete stable snapshot for the owned fixture.");
    }
    private static async Task Wait(Func<bool> ready)
    {
        var started = Stopwatch.StartNew();
        while (!ready())
        {
            if (started.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Real Explorer notification/reconciliation did not complete.");
            await Task.Delay(100);
        }
    }
    private static void Check(bool success, string message)
    { if (!success) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
    private sealed class SmokeLogger<T>(string path) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            lock (LogGate) File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {level} {typeof(T).Name}: {formatter(state, error)}{Environment.NewLine}" +
                (error is null ? "" : error + Environment.NewLine));
        }
    }
}
