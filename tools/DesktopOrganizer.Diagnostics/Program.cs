using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopOrganizer.Infrastructure.Interop;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using DesktopOrganizer.Core.Models;
using Microsoft.Extensions.Logging;

try { return await RunAsync(args); }
catch (Exception error)
{
    // A missing desktop/tray in a restricted session is a failed diagnostic,
    // not an unhandled .NET crash/dialog on the user's desktop.
    Console.Error.WriteLine($"DesktopOrganizer diagnostic failed: {error.GetType().Name}: {error.Message}");
    return 1;
}

static async Task<int> RunAsync(string[] args)
{
var discovery = new DesktopDiscovery(NullLogger<DesktopDiscovery>.Instance);
if (args.Length == 2 && args[0] is "--tray-state" or "--tray-open" or "--tray-exit" or "--tray-menu")
{
    Console.WriteLine(DesktopDiagnostics.TrayState(uint.Parse(args[1]), open: args[0] == "--tray-open", exit: args[0] == "--tray-exit", menu: args[0] == "--tray-menu"));
    return 0;
}
var desktop = discovery.Find();
if (desktop is null) { Console.Error.WriteLine("No validated Explorer desktop host. " + discovery.LastDiagnostics); return 1; }
Console.WriteLine(desktop);
if (args.Length == 1 && args[0] == "--desktop-state")
{
    Console.WriteLine("ListView region=" + DesktopDiagnostics.IconRegion(desktop));
    foreach (var line in DesktopDiagnostics.WallpaperWindowTree(desktop)) Console.WriteLine(line);
    foreach (var area in DesktopDiagnostics.MonitorAreas())
        foreach (var point in new[] { new DesktopPoint(area.X + 20, area.Y + 20), new DesktopPoint(area.X + area.Width / 2, area.Y + area.Height / 2), new DesktopPoint(area.X + area.Width - 100, area.Y + area.Height - 100) })
            Console.WriteLine($"Hit at {point.X},{point.Y}: {DesktopDiagnostics.WindowAt(point)}");
    return 0;
}
if (args.Length == 2 && args[0] == "--recover-desktop")
{
    foreach (var process in System.Diagnostics.Process.GetProcessesByName("DesktopOrganizer.App"))
        using (process) throw new InvalidOperationException("Exit DesktopOrganizer before recovery.");
    if (DesktopDiagnostics.Inspect(desktop).Count != 0) throw new InvalidOperationException("Live Fence surfaces prevent recovery.");
    var directory = Path.GetFullPath(args[1]);
    // Reuse the HWND/PID-validated crash journal; never guess another renderer's original properties.
    using (var host = new Win32DesktopHost(discovery, NullLogger<Win32DesktopHost>.Instance, Path.Combine(directory, "desktop-presentation.json")))
        if (!host.Connect()) throw new InvalidOperationException("Explorer recovery host unavailable.");
    Console.WriteLine("Original Explorer presentation restored: " + DesktopDiagnostics.CompositionState(desktop));
    using var icons = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
    var snapshot = await icons.ReadAsync();
    if (!snapshot.Connected || !snapshot.CanPosition || snapshot.Error is not null) throw new InvalidOperationException("Complete recovery snapshot unavailable.");
    var journal = new JsonIconRecoveryStore(directory, NullLogger<JsonIconRecoveryStore>.Instance);
    var present = FenceIconLayout.Match(snapshot.Icons);
    var monitors = DesktopDiagnostics.MonitorAreas();
    var targets = journal.Load().Where(t => present.TryGetValue(t.StableId, out var icon) &&
        !monitors.Any(m => icon.Bounds.X < m.X + m.Width && icon.Bounds.X + icon.Bounds.Width > m.X &&
            icon.Bounds.Y < m.Y + m.Height && icon.Bounds.Y + icon.Bounds.Height > m.Y)).ToArray();
    if (targets.Length != 0) await icons.PositionDropAsync(targets);
    if (!IconPlacement.AtTargets(await icons.ReadAsync(), targets)) throw new InvalidOperationException("Recovery position readback failed; journal retained.");
    journal.Save([]);
    Console.WriteLine($"Returned {targets.Length} parked icons to recorded return positions; visible desktop items unchanged.");
    return 0;
}
if (args.Length == 1 && args[0] == "--redraw-visibility")
{
    using var icons = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
    foreach (var state in await icons.ProbeRedrawVisibilityAsync()) Console.WriteLine(state);
    return 0;
}
if (args.Length == 1 && args[0] == "--composition-lease")
{
    foreach (var state in DesktopDiagnostics.CheckMarqueeCompositionLease()) Console.WriteLine(state);
    Console.WriteLine("PASS: real Explorer composition lease roundtrip; no mouse/marquee/OLE gesture replayed.");
    return 0;
}
if (args.Length == 1 && args[0] == "--presentation-state")
{
    Console.WriteLine(DesktopDiagnostics.CompositionState(desktop)); return 0;
}
if (args.Length == 2 && args[0] == "--wallpaper-composition-probe")
    return await WallpaperCompositionProbe(args[1]);
if (args.Length == 3 && args[0] == "--selection-at")
{
    using var icons = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
    var selection = await icons.ReadSelectionAsync(new(double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture),
        double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)));
    Console.WriteLine(JsonSerializer.Serialize(new { SelectedCount = selection.SelectedIds.Count, HitItem = selection.DraggedItemId is not null }));
    return 0;
}
if (args.Length == 1 && args[0] == "--drop-batch-roundtrip")
{
    if (DesktopDiagnostics.Inspect(desktop).Count != 0) throw new InvalidOperationException("Close DesktopOrganizer before the reversible icon batch probe.");
    var trace = new ExplorerInteractionTrace(new DiagnosticTraceLogger());
    using var icons = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance, trace);
    var snapshot = await icons.ReadAsync();
    if (!snapshot.CanPosition || snapshot.Error is not null) throw new InvalidOperationException("Positioning unavailable.");
    var source = snapshot.Icons.Where(i => i.Position.X >= 0 && i.Position.Y >= 0).Take(5).ToArray();
    if (source.Length != 5) throw new InvalidOperationException("Five visible actual items are required.");
    var original = snapshot.Icons.Select(i => new DesktopIconTarget(i.StableId, i.Position)).ToArray();
    var targets = source.Select((i, n) => new DesktopIconTarget(i.StableId, new(1900 + n % 3 * snapshot.Metrics.StepX, 900 + n / 3 * snapshot.Metrics.StepY))).ToArray();
    try
    {
        var before = icons.PositionCounters;
        trace.Begin();
        await icons.ReconcileDropAsync(_ => []);
        var noOp = icons.PositionCounters;
        if (noOp != before || trace.Snapshot.Any(p => p.Stage.StartsWith("SetRedraw", StringComparison.Ordinal)))
            throw new InvalidOperationException("An assignment-only/no-op drop acquired a redraw lease.");
        trace.Complete("PASS: actual empty native plan; 0 bulk / 0 redraw");
        trace.Begin();
        await icons.ReconcileDropAsync(final =>
        {
            trace.Mark("Layout.compute", source.Length);
            return FenceIconLayout.ChangedTargets(targets, final.Icons);
        });
        if (!IconPlacement.AtTargets(await icons.ReadAsync(), targets)) throw new InvalidOperationException("Actual Shell batch did not retain its positions.");
        var after = icons.PositionCounters;
        Console.WriteLine($"Five real items: BulkCalls={after.Batches-before.Batches}; RedrawCompletions={after.RedrawCompletions-before.RedrawCompletions}");
        if (after.Batches-before.Batches != 1 || after.RedrawCompletions-before.RedrawCompletions != 1) throw new InvalidOperationException("More than one visual batch.");
        trace.Complete("actual Shell API reconciliation; no physical mouse/OLE gesture");
    }
    finally
    {
        await icons.PositionDropAsync(original);
        icons.Dispose();
        using var verifier = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
        if (!IconPlacement.AtTargets(await verifier.ReadAsync(), original)) throw new InvalidOperationException("Original icon positions were not restored.");
    }
    Console.WriteLine("PASS: real Shell bulk positioning + original positions restored. Native mouse/OLE drag was not tested.");
    return 0;
}
if (args.Length == 1 && args[0] == "--input-state")
{
    Console.WriteLine(JsonSerializer.Serialize(DesktopDiagnostics.InputState(desktop)));
    Console.WriteLine(JsonSerializer.Serialize(DesktopDiagnostics.MonitorAreas()));
    return 0;
}
if (args.Length == 2 && args[0] == "--snapshot-positions")
{
    using var icons = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
    var snapshot = await icons.ReadAsync();
    if (!snapshot.Connected) throw new InvalidOperationException("No connected desktop icon reader.");
    File.WriteAllText(args[1], JsonSerializer.Serialize(snapshot.Icons.Select(i => new DesktopIconTarget(i.StableId, i.Position))));
    Console.WriteLine($"Saved {snapshot.Icons.Count} actual icon positions without moving icons.");
    return 0;
}
if (args.Length == 2 && args[0] == "--motion-capture")
{
    Directory.CreateDirectory(args[1]);
    using var icons = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
    var snapshot = await icons.ReadAsync();
    var icon = snapshot.Icons.First(i => i.StableId.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase));
    DesktopShell.ShowDesktop(); await Task.Delay(200);
    void Capture(string name)
    {
        var pixels = DesktopDiagnostics.CaptureScreenArea(new(0, 0, 1600, 1000));
        var bitmap = BitmapSource.Create(1600, 1000, 96, 96, PixelFormats.Bgr32, null, pixels, 6400);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(args[1], name + ".png")); encoder.Save(file);
    }
    try
    {
        Capture("before");
        await icons.PositionAsync([new(icon.StableId, new(1000, 500))]);
        await Task.Delay(100);
        Console.WriteLine((await icons.ReadAsync()).Icons.Single(i => i.StableId == icon.StableId).Position);
        Capture("after");
    }
    finally { await icons.PositionAsync([new(icon.StableId, icon.Position)]); }
    return 0;
}
if (args.Length == 2 && args[0] == "--capture-desktop-corner")
{
    DesktopShell.ShowDesktop(); Thread.Sleep(200);
    var pixels = DesktopDiagnostics.CaptureScreenArea(new(0, 0, 500, 750));
    var bitmap = BitmapSource.Create(500, 750, 96, 96, PixelFormats.Bgr32, null, pixels, 2000);
    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var file = File.Create(args[1]); encoder.Save(file); return 0;
}
if (args.Length == 2 && args[0] == "--background-probe")
{
    Directory.CreateDirectory(args[1]);
    using var host = new Win32DesktopHost(discovery, NullLogger<Win32DesktopHost>.Instance);
    host.Connect();
    var fence = new Fence(Guid.NewGuid(), "Background probe", 600, 120, 450, 350);
    var layout = FenceSurfaceLayout.Clip(fence, host.GetArea(), host.GetScale(fence))!.Value;
    host.Update(fence, layout, host.GetScale(fence), null);
    DesktopShell.ShowDesktop();
    Thread.Sleep(200);
    foreach (var mode in new[] { "painted", "hidden" })
    {
        DesktopDiagnostics.ShowProbeBackgrounds(host, mode == "painted"); host.EnsureStacking();
        Console.WriteLine(mode + ": " + string.Join("; ", DesktopDiagnostics.Layers(host.Handles!)));
        DesktopDiagnostics.RepaintProbe(host); host.FlushPainting(); Thread.Sleep(100);
        var pixels = DesktopDiagnostics.CaptureScreenArea(new(600, 120, 450, 350));
        var bitmap = BitmapSource.Create(450, 350, 96, 96, PixelFormats.Bgr32, null, pixels, 450 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(args[1], mode + ".png")); encoder.Save(file);
    }
    Console.WriteLine("Captured real desktop pixels with the Fence background visible/hidden.");
    return 0;
}
if (args.Length == 3 && args[0] == "--remember-assigned")
{
    using var icons = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
    var snapshot = await icons.ReadAsync();
    var ids = new JsonFenceStore(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, NullLogger<JsonFenceStore>.Instance)
        .Load().Fences.SelectMany(f => f.Icons).Select(i => i.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var original = snapshot.Icons.Where(i => ids.Contains(i.StableId)).Select(i => new DesktopIconTarget(i.StableId, i.Position)).ToArray();
    File.WriteAllText(args[2], JsonSerializer.Serialize(original));
    Console.WriteLine($"Remembered {original.Length} assigned icon positions without moving them.");
    return 0;
}
if (args.Length == 2 && args[0] == "--restore-positions")
{
    using var icons = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
    var before = await icons.ReadAsync();
    if (!before.Connected || !before.CanPosition) return 1;
    var original = JsonSerializer.Deserialize<DesktopIconTarget[]>(File.ReadAllText(args[1])) ?? throw new InvalidDataException();
    await icons.PositionAsync(original);
    var after = await icons.ReadAsync();
    foreach (var target in original)
        if (after.Icons.Single(i => i.StableId == target.StableId).Position != target.Position) throw new InvalidOperationException("Icon restoration failed.");
    Console.WriteLine($"Restored {original.Length} remembered icon positions.");
    return 0;
}
if (args.Length == 2 && args[0] == "--verify-assigned")
{
    using var icons = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
    var snapshot = await icons.ReadAsync();
    var groups = new JsonFenceStore(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, NullLogger<JsonFenceStore>.Instance).Load().Fences;
    var targets = IconPlacement.Targets(groups, DesktopDiagnostics.MonitorAreas(), snapshot.Metrics);
    if (!IconPlacement.AtTargets(snapshot, targets)) throw new InvalidOperationException("Real icon positions do not match persisted normal/parked assignments.");
    var collapsedCount = groups.Where(f => f.IsCollapsed).Sum(f => f.Icons.Length);
    Console.WriteLine($"Verified {targets.Count} real Explorer icon positions against persisted assignments; {collapsedCount} parked for collapsed Fences.");
    return 0;
}
if (args.Length == 1 && args[0] is "--icons" or "--position-roundtrip" or "--offscreen-roundtrip")
{
    using var icons = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
    var snapshot = await icons.ReadAsync();
    Console.WriteLine(JsonSerializer.Serialize(new { snapshot.Connected, snapshot.CanPosition, Count = snapshot.Icons.Count, snapshot.Metrics, snapshot.Error }));
    if (!snapshot.Connected || snapshot.Icons.Count == 0) return 1;
    if (args[0] == "--icons") return 0;
    if (!snapshot.CanPosition) return 1;
    var icon = snapshot.Icons.First(i => i.StableId.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase));
    var target = args[0] == "--offscreen-roundtrip" ? new DesktopPoint(-8192, -8192) : new DesktopPoint(icon.Position.X + 46, icon.Position.Y + 46);
    try
    {
        await icons.PositionAsync([new(icon.StableId, target)]);
        await Task.Delay(250);
        var moved = (await icons.ReadAsync()).Icons.Single(i => i.StableId == icon.StableId);
        Console.WriteLine($"Actual Explorer move: before={icon.Position}; requested={target}; actual={moved.Position}; same identity={moved.StableId == icon.StableId}");
        if (Math.Abs(moved.Position.X - target.X) + Math.Abs(moved.Position.Y - target.Y) > 2) return 1;
    }
    finally
    {
        await icons.PositionAsync([new(icon.StableId, icon.Position)]);
        await Task.Delay(250);
        var restored = (await icons.ReadAsync()).Icons.Single(i => i.StableId == icon.StableId);
        Console.WriteLine($"Restored actual Explorer position: {restored.Position}");
        if (restored.Position != icon.Position) throw new InvalidOperationException("Icon position restoration failed.");
    }
    return 0;
}
var surfaces = DesktopDiagnostics.Inspect(desktop);
Console.WriteLine(JsonSerializer.Serialize(surfaces, new JsonSerializerOptions { WriteIndented = true }));
if (args.Length == 2 && args[0] == "--capture-directory")
{
    Directory.CreateDirectory(args[1]);
    foreach (var surface in surfaces)
    {
        var area = surface.Bounds;
        var pixels = DesktopDiagnostics.CaptureScreenArea(area);
        var bitmap = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgr32, null, pixels, area.Width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(args[1], $"surface-{surface.Hwnd}.png");
        using var file = File.Create(path);
        encoder.Save(file);
        Console.WriteLine($"Actual screen capture: {path}");
    }
}
return 0;

}

static Task<int> WallpaperCompositionProbe(string directory)
{
    var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                var discovery = new DesktopDiscovery(NullLogger<DesktopDiscovery>.Instance);
                var handles = discovery.Find() ?? throw new InvalidOperationException("No Explorer desktop.");
                if (DesktopDiagnostics.Inspect(handles).Count != 0)
                    throw new InvalidOperationException("Close DesktopOrganizer before the reversible wallpaper probe.");
                var before = DesktopDiagnostics.CompositionState(handles);
                foreach (var line in DesktopDiagnostics.WallpaperWindowTree(handles)) Console.WriteLine(line);
                DesktopShell.ShowDesktop();
                await Task.Delay(150);
                var area = DesktopDiagnostics.MonitorAreas().First();
                var sample = new DesktopArea(area.X + area.Width / 2, area.Y + area.Height * 3 / 4, 180, 100);
                foreach (var mode in new[] { "original", "body-only", "header-only", "key-only", "full-key", "key-transparent", "native-transparent" })
                {
                    using (var host = new Win32DesktopHost(discovery, NullLogger<Win32DesktopHost>.Instance))
                    {
                        if (!host.Connect()) throw new InvalidOperationException("Desktop host lost.");
                        DesktopDiagnostics.SetProbeIconComposition(host, false);
                        var fence = new Fence(Guid.NewGuid(), "Wallpaper composition probe", Math.Round(area.X + area.Width * .65), Math.Round(area.Y + area.Height * .25), 300, 220);
                        var scale = host.GetScale(fence);
                        var layout = FenceSurfaceLayout.Clip(fence, host.GetArea(), scale) ?? throw new InvalidOperationException("No probe bounds.");
                        if (mode != "original")
                        {
                            var headerHeight = Math.Min(layout.Height, (int)Math.Ceiling(FenceInteractionGeometry.HeaderHeight * scale));
                            var header = new byte[layout.Width * headerHeight * 4];
                            for (var i = 0; i < header.Length; i += 4)
                            { header[i] = 0x5A; header[i + 1] = 0x41; header[i + 2] = 0x2D; header[i + 3] = 255; }
                            host.Update(fence, layout, scale, header);
                        }
                        DesktopDiagnostics.SetWallpaperProbeMode(host, mode);
                        await Task.Delay(150); // Dispatcher continues serving only the probe's owned HWNDs.
                        Capture(mode + "-wallpaper", sample);
                        Capture(mode + "-fence", new(layout.X, layout.Y, layout.Width, layout.Height));
                        Console.WriteLine(mode + ": " + DesktopDiagnostics.CompositionState(handles));
                    }
                    await Task.Delay(100);
                    if (DesktopDiagnostics.CompositionState(handles) != before)
                        throw new InvalidOperationException("The probe did not restore original Explorer properties after " + mode);
                }
                Capture("restored-wallpaper", sample);
                Console.WriteLine("PASS: original properties restored after every mode; no files/icons moved; pixel comparison still required.");
                completion.SetResult(0);

                void Capture(string name, DesktopArea captureArea)
                {
                    var pixels = DesktopDiagnostics.CaptureScreenArea(captureArea);
                    var bitmap = BitmapSource.Create(captureArea.Width, captureArea.Height, 96, 96, PixelFormats.Bgr32, null, pixels, captureArea.Width * 4);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
                    var black = 0;
                    for (var i = 0; i < pixels.Length; i += 4)
                        if (pixels[i] < 8 && pixels[i + 1] < 8 && pixels[i + 2] < 8) black++;
                    Console.WriteLine($"{name}: black={(double)black / (captureArea.Width * captureArea.Height):P1}");
                }
            }
            catch (Exception error) { completion.SetException(error); }
            finally { dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send); }
        }));
        System.Windows.Threading.Dispatcher.Run();
    }) { IsBackground = true, Name = "DesktopOrganizer wallpaper composition probe" };
    thread.SetApartmentState(ApartmentState.STA); thread.Start();
    return completion.Task;
}

internal sealed class DiagnosticTraceLogger : ILogger<ExplorerInteractionTrace>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Console.WriteLine(formatter(state, exception));
}
