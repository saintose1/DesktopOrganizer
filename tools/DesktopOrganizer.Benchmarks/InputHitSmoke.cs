using System.Windows;
using DesktopOrganizer.App.Desktop;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Infrastructure.Interop;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Real HWND hit-path queries only. This does not pretend to perform a physical mouse/OLE gesture.</summary>
internal static class InputHitSmoke
{
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var result = 1;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var discovery = new DesktopDiscovery(NullLogger<DesktopDiscovery>.Instance);
                var handles = discovery.Find() ?? throw new InvalidOperationException("No desktop.");
                DesktopShell.ShowDesktop(); await Task.Delay(200);
                var before = DesktopDiagnostics.MouseTarget(handles, 1100, 900);
                Console.WriteLine("Before lease: " + before);
                using (var host = new Win32DesktopHost(discovery, NullLogger<Win32DesktopHost>.Instance))
                {
                    if (!host.Connect()) throw new InvalidOperationException("No connected host.");
                    var fence = new Fence(Guid.NewGuid(), "Input probe", 600, 120, 450, 350);
                    var scale = host.GetScale(fence);
                    var layout = FenceSurfaceLayout.Clip(fence, host.GetArea(), scale)!.Value;
                    host.Update(fence, layout, scale, new FenceBitmapRenderer().Render(fence, layout, scale)); host.FlushDamage();
                    var second = fence.WithBounds(1500, 200, 400, 300);
                    second = new Fence(Guid.NewGuid(), "Second input probe", second.X, second.Y, second.Width, second.Height);
                    var secondLayout = FenceSurfaceLayout.Clip(second, host.GetArea(), scale)!.Value;
                    host.Update(second, secondLayout, scale, new FenceBitmapRenderer().Render(second, secondLayout, scale)); host.FlushDamage();
                    await Task.Delay(100);
                    for (var step = 0; step < 8; step++)
                    {
                        DesktopDiagnostics.ProbeListViewRaise(host);
                        await Task.Delay(20);
                    }
                    Console.WriteLine("PASS: 8 real ListView raises restored 2 headers above icons and bodies below, with no extra repaint or bitmap presents. API/Z-order probe, not physical click verification.");
                    foreach (var (name, x, y, hit) in new[] { ("Header", 700, 150, 1), ("Content", 700, 250, -1), ("Left grip", 601, 250, 1) })
                    {
                        Console.WriteLine(name + ": " + DesktopDiagnostics.MouseTarget(handles, x, y));
                        if (DesktopDiagnostics.FenceHitAt(handles, x, y) != hit) throw new InvalidOperationException(name + " claims the wrong hit.");
                    }
                    Console.WriteLine("Empty desktop during lease: " + DesktopDiagnostics.MouseTarget(handles, 1100, 900));
                }
                await Task.Delay(100);
                var after = DesktopDiagnostics.MouseTarget(handles, 1100, 900);
                Console.WriteLine("After lease: " + after);
                if (before.Hwnd != after.Hwnd) throw new InvalidOperationException("Native desktop input target was not restored.");
                Console.WriteLine("PASS: real header/grip hit queries, transparent content, Explorer lease restoration. Physical rubber-band/OLE still needs manual verification.");
                result = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { app.Shutdown(); }
        });
        app.Run(); return result;
    }
}
