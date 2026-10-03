using System.Diagnostics;
using System.Reflection;
using System.Windows;
using DesktopOrganizer.App.Desktop;
using DesktopOrganizer.App.ViewModels;
using DesktopOrganizer.App.Views;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Core.Storage;
using DesktopOrganizer.Infrastructure.Interop;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--composition-ab") return CompositionAB.Run();
        if (args.Length == 1 && args[0] == "--outline-composition")
        {
            foreach (var line in DesktopDiagnostics.CheckMarqueeCompositionLease(outline: true)) Console.WriteLine(line);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--selection-enumeration")
        {
            using var reader = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
            Console.WriteLine(reader.ProbeSelectionEnumerationAsync().GetAwaiter().GetResult());
            return 0;
        }
        if (args.Length == 1 && args[0] == "--input-routing-lifecycle")
        {
            var ready = DesktopDiagnostics.CheckBackgroundMouseRoutingLifetime();
            Console.WriteLine(ready ? "PASS: native mouse hook/message-loop created and disposed; all input passed through. No Explorer gestures tested." : "FAIL: input routing could not start.");
            return ready ? 0 : 1;
        }
        if (args.Length == 1 && args[0] == "--icons-smoke") return DesktopSmoke.Run();
        if (args.Length == 1 && args[0] == "--rendering-smoke") return RenderingSmoke.Run();
        if (args.Length == 1 && args[0] == "--rendering-smoke-background") return RenderingSmoke.Run(showDesktop: false);
        if (args.Length == 1 && args[0] == "--animation-cadence") return AnimationCadenceSmoke.Run();
        if (args.Length == 1 && args[0] == "--input-hit-smoke") return InputHitSmoke.Run();
        var app = new Application();
        var discovery = new DesktopDiscovery(NullLogger<DesktopDiscovery>.Instance);
        using var host = new Win32DesktopHost(discovery, NullLogger<Win32DesktopHost>.Instance);
        using var desktop = new DesktopIntegrationService(host, new FenceBitmapRenderer(), NullLogger<DesktopIntegrationService>.Instance);
        var original = new Fence(Guid.NewGuid(), "Performance probe", 600, 120, 450, 350);
        var vm = new MainWindowViewModel(new ProbeStore(original), NullLogger<MainWindowViewModel>.Instance, desktop);
        var window = new MainWindow(vm);
        window.Show();
        desktop.Start();
        if (!desktop.Connection.Connected) { Console.Error.WriteLine(desktop.Connection.Error); window.Close(); return 1; }
        var update = typeof(Win32DesktopHost).GetMethod("NotifyInteraction", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<FenceInteractionEventArgs>>(host);
        var renderer = new FenceBitmapRenderer();
        Measure("GetArea + GetScale", i => { _ = host.GetArea(); _ = host.GetScale(original); });
        Measure("Cached header 450x36", i => renderer.Render(original, new(600, 120, 450, 350), 1));
        foreach (var resize in new[] { false, true })
        {
            var hit = resize ? FenceHit.Right | FenceHit.Bottom : FenceHit.Header;
            update(new(original, InteractionPhase.Started) { Hit = hit });
            Measure(resize ? "Native resize pipeline + GDI paint" : "Native drag pipeline + GDI paint", i =>
            {
                var next = original.WithBounds(600 + i % 120, 120 + i % 60, 450 + (resize ? i % 180 : 0), 350 + (resize ? i % 120 : 0));
                update(new(next, InteractionPhase.Preview) { Hit = hit });
                host.FlushPainting();
                window.UpdateLayout();
            });
            update(new(original, InteractionPhase.Cancelled));
        }
        window.Close();
        return 0;
    }
    private static void Measure(string label, Action<int> action)
    {
        for (var i = 0; i < 20; i++) action(i);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        double total = 0, maximum = 0;
        var duration = Stopwatch.StartNew();
        const int count = 240;
        for (var i = 0; i < count; i++)
        {
            var start = Stopwatch.GetTimestamp(); action(i);
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            total += ms; maximum = Math.Max(maximum, ms);
        }
        Console.WriteLine($"{label}: mean={total/count:F3}ms; max={maximum:F3}ms; updates/sec={count/duration.Elapsed.TotalSeconds:F1}; allocated/update={(GC.GetAllocatedBytesForCurrentThread()-allocated)/count:N0} bytes");
    }
    private sealed class ProbeStore(Fence fence) : IFenceStore
    {
        public FenceLoadResult Load() => new([fence]);
        public void Save(IReadOnlyCollection<Fence> fences) => throw new InvalidOperationException("A preview benchmark must never save.");
    }
}
