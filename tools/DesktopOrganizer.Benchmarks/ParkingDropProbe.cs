using System.Diagnostics;
using System.IO;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Infrastructure.Interop;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Two owned fixtures only; real mouse/OLE is supplied by the user. No user item positions are written.</summary>
internal static class ParkingDropProbe
{
    public static int Run()
    {
        try { return Scenario().GetAwaiter().GetResult(); }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
    private static async Task<int> Scenario()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var prefix = "DesktopOrganizer-probe-" + Guid.NewGuid().ToString("N");
        var owned = new List<string>();
        using var icons = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
        try
        {
            foreach (var kind in new[] { "negative", "positive" })
            {
                var path = Path.Combine(desktop, prefix + "-" + kind + ".txt");
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                { owned.Add(path); file.Write("DesktopOrganizer temporary parking diagnostic."u8); }
                DesktopDiagnostics.NotifySmokeItemChange(path);
            }
            await Healthy(s => owned.All(path => s.Icons.Any(i => FenceIconLayout.IdentityComparer.Equals(i.StableId, path))));
            var areas = DesktopDiagnostics.MonitorAreas();
            DesktopIconTarget[] targets = [new(owned[0], new(areas.Min(a => a.X) - 8192, areas.Min(a => a.Y) - 8192)),
                new(owned[1], new(areas.Max(a => a.X + a.Width) + 8192, areas.Max(a => a.Y + a.Height) + 8192))];
            await icons.PositionAsync(targets);
            await Healthy(s => IconPlacement.AtTargets(s, targets));
            Console.WriteLine("READY: two owned TXT fixtures parked negative/positive. Drag an ordinary desktop item once; user item positions are never written by this probe.");
            var handles = new DesktopDiscovery(NullLogger<DesktopDiscovery>.Instance).Find() ?? throw new InvalidOperationException("No desktop.");
            var watch = Stopwatch.StartNew();
            var down = false; var moved = false; DesktopInputDiagnostic? press = null;
            while (watch.Elapsed < TimeSpan.FromMinutes(3))
            {
                var input = DesktopDiagnostics.InputState(handles);
                if (input.LeftButtonDown && !down)
                {
                    var hit = DesktopDiagnostics.WindowAt(new(input.X, input.Y));
                    if (hit.Contains("SysListView32") || hit.Contains("SHELLDLL_DefView"))
                    { down = true; press = input; }
                }
                if (down && press is not null && Math.Abs(input.X - press.X) + Math.Abs(input.Y - press.Y) > 12) moved = true;
                if (down && !input.LeftButtonDown)
                {
                    if (moved)
                    {
                        await Task.Delay(150);
                        var after = await Healthy(_ => true);
                        foreach (var target in targets)
                        {
                            var actual = after.Icons.Single(i => FenceIconLayout.IdentityComparer.Equals(i.StableId, target.StableId));
                            Console.WriteLine($"{(target.StableId == owned[0] ? "NEGATIVE" : "POSITIVE")}: stayed={actual.Position == target.Position}; actual={actual.Position.X},{actual.Position.Y}");
                        }
                        return 0;
                    }
                    down = false; press = null;
                }
                await Task.Delay(50);
            }
            throw new TimeoutException("No real desktop drag observed; parking candidate is unverified.");
        }
        finally
        {
            foreach (var path in owned)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(path), desktop) || !Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing cleanup of an unowned file.");
                File.Delete(path); DesktopDiagnostics.NotifySmokeItemChange(path, deleted: true);
            }
            Console.WriteLine("Owned fixtures removed; no user files deleted or user positions restored/reset.");
        }
        async Task<DesktopIconSnapshot> Healthy(Func<DesktopIconSnapshot, bool> ready)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(12))
            {
                var snapshot = await icons.ReadAsync();
                if (snapshot.Connected && snapshot.CanPosition && snapshot.Error is null && ready(snapshot)) return snapshot;
                await Task.Delay(100);
            }
            throw new TimeoutException("Explorer did not publish the owned fixtures consistently.");
        }
    }
}
