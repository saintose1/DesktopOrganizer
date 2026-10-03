using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopOrganizer.App.Desktop;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Infrastructure.Interop;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Manual layer isolation. Does not synthesize mouse/OLE or move any Explorer item.</summary>
internal static class CompositionAB
{
    public static int Run()
    {
        var discovery = new DesktopDiscovery(NullLogger<DesktopDiscovery>.Instance);
        var handles = discovery.Find() ?? throw new InvalidOperationException("No desktop.");
        if (DesktopDiagnostics.Inspect(handles).Count != 0) throw new InvalidOperationException("Close DesktopOrganizer first.");
        var original = DesktopDiagnostics.CompositionState(handles);
        using var icons = new DesktopIconService(NullLogger<DesktopIconService>.Instance, NullLogger<DesktopDiscovery>.Instance);
        var directory = Path.GetFullPath(Path.Combine(".tools", "composition-ab", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(directory);
        Win32DesktopHost? host = null;
        var app = new Application();
        var panel = new StackPanel { Margin = new Thickness(16) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        var window = new Window { Title = "DesktopOrganizer — A/B composition diagnostics", Content = panel, Width = 620, Height = 560 };
        panel.Children.Add(new TextBlock { Text = "Только диагностика слоёв. Реальные иконки не перемещаются.", TextWrapping = TextWrapping.Wrap });
        var stages = new[] { "0 — Без surfaces / исходный Explorer", "1 — Только ListView color key", "2 — Только background / исходный ListView",
            "3 — Только header / исходный ListView", "4 — Полный Fence / прежний alpha color key", "5 — Полный Fence / прежний native guard",
            "6 — Полный Fence / контурная рамка, буферизация icons" };
        var selected = 0;
        void Stage(int index)
        {
            host?.Dispose(); selected = index;
            host = new(discovery, NullLogger<Win32DesktopHost>.Instance);
            if (!host.Connect()) throw new InvalidOperationException("Host not connected.");
            host.SetSelectionReader(icons);
            DesktopDiagnostics.SetProbeNativeSelection(host, index is 5 or 6);
            DesktopDiagnostics.SetProbeOutlineSelection(host, index == 6);
            if (index != 0)
            {
                if (index is 2 or 3) DesktopDiagnostics.SetProbeIconComposition(host, false);
                var fence = new Fence(Guid.NewGuid(), "A/B Fence", 700, 320, 600, 450);
                var scale = host.GetScale(fence); var layout = FenceSurfaceLayout.Clip(fence, host.GetArea(), scale)!.Value;
                host.Update(fence, layout, scale, new FenceBitmapRenderer().Render(fence, layout, scale));
                DesktopDiagnostics.ShowOwnProbeLayers(host, index is 2 or 4 or 5 or 6, index is 3 or 4 or 5 or 6);
                host.EnsureStacking(); host.FlushPainting();
            }
            var text = stages[index] + "\n" + host.Diagnostics + "\n" + DesktopDiagnostics.CompositionState(handles);
            status.Text = text; File.AppendAllText(Path.Combine(directory, "stages.txt"), DateTimeOffset.Now + " " + text + "\n");
        }
        for (var i = 0; i < stages.Length; i++)
        {
            var index = i; var button = new Button { Content = stages[i], Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(4) };
            button.Click += (_, _) =>
            {
                try { Stage(index); }
                catch (Exception error) { status.Text = error.ToString(); File.AppendAllText(Path.Combine(directory, "errors.txt"), error + "\n"); }
            };
            panel.Children.Add(button);
        }
        panel.Children.Add(status);
        var show = new Button { Content = "Показать рабочий стол", Padding = new Thickness(6) };
        show.Click += (_, _) => DesktopShell.ShowDesktop(); panel.Children.Add(show);
        var capture = new Button { Content = "Сохранить реальные кадры (5 секунд)", Padding = new Thickness(6), Margin = new Thickness(0, 5, 0, 0) };
        capture.Click += async (_, _) =>
        {
            capture.IsEnabled = false;
            for (var i = 0; i < 50; i++)
            {
                var area = new DesktopArea(0, 0, 1600, 1000); var pixels = DesktopDiagnostics.CaptureScreenArea(area);
                var bitmap = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgr32, null, pixels, area.Width*4);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(directory, $"stage-{selected}-frame-{i:D2}.png"))) encoder.Save(file);
                await Task.Delay(100);
            }
            capture.IsEnabled = true;
        };
        panel.Children.Add(capture);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => { if (host is not null && !host.HasExplorerGesture && !host.IsExplorerDragging && !host.IsLeftButtonDown) host.FinishNativeSelection(); };
        timer.Start(); Stage(0);
        try { app.Run(window); }
        finally
        {
            timer.Stop(); host?.Dispose();
            if (DesktopDiagnostics.CompositionState(handles) != original) throw new InvalidOperationException("Original ListView properties were not restored.");
        }
        Console.WriteLine("Manual A/B artifacts: " + directory); return 0;
    }
}
