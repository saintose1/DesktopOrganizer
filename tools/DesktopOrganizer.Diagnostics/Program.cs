using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopOrganizer.Infrastructure.Interop;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using DesktopOrganizer.Core.Models;

var discovery = new DesktopDiscovery(NullLogger<DesktopDiscovery>.Instance);
var desktop = discovery.Find();
if (desktop is null) { Console.Error.WriteLine("No validated Explorer desktop host. " + discovery.LastDiagnostics); return 1; }
Console.WriteLine(desktop);
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
