using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 48, 64, 128, 256];
    [STAThread]
    private static void Main(string[] args)
    {
        var directory = Path.GetFullPath(args.Length == 1 ? args[0] : "src/DesktopOrganizer.App/Assets/Branding");
        var source = new BitmapImage(new Uri(Path.Combine(directory, "dog-original.png")));
        var main = Sizes.Select(size => Render(size, dc => dc.DrawImage(source, new Rect(0, 0, 64, 64)))).ToArray();
        var tray = Sizes.Select(size => Render(size, DrawDog)).ToArray();
        WriteIcon(Path.Combine(directory, "application.ico"), main);
        WriteIcon(Path.Combine(directory, "tray.ico"), tray);
        File.WriteAllBytes(Path.Combine(directory, "tray-preview.png"), tray[5]);
        Console.WriteLine("Generated application.ico / tray.ico: " + string.Join(", ", Sizes));
    }
    private static byte[] Render(int size, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { dc.PushTransform(new ScaleTransform(size / 64.0, size / 64.0)); draw(dc); dc.Pop(); }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static void DrawDog(DrawingContext dc)
    {
        var purple = new SolidColorBrush(Color.FromRgb(191, 170, 255));
        var white = new SolidColorBrush(Color.FromRgb(245, 241, 255));
        var dark = new SolidColorBrush(Color.FromRgb(20, 17, 30));
        // Vector interpretation of the supplied dog: floppy ears, pale face, dark eyes/nose.
        dc.DrawGeometry(purple, null, Geometry.Parse("M18,11 C7,9 3,17 4,30 L5,44 Q7,51 14,45 L20,31 Z M46,11 C57,9 61,17 60,30 L59,44 Q57,51 50,45 L44,31 Z"));
        dc.DrawGeometry(white, new Pen(purple, 2), Geometry.Parse("M32,7 C18,7 12,18 14,34 C14,50 22,57 32,57 C42,57 50,50 50,34 C52,18 46,7 32,7 Z"));
        dc.DrawGeometry(dark, null, Geometry.Parse("M16,15 Q10,23 14,39 Q19,39 23,27 Q24,20 16,15 Z M48,15 Q54,23 50,39 Q45,39 41,27 Q40,20 48,15 Z"));
        dc.DrawEllipse(dark, null, new Point(24, 27), 3.5, 4);
        dc.DrawEllipse(dark, null, new Point(40, 27), 3.5, 4);
        dc.DrawEllipse(dark, null, new Point(32, 41), 8, 6);
        dc.DrawGeometry(null, new Pen(dark, 2), Geometry.Parse("M32,45 L32,49 M25,49 Q32,54 39,49"));
    }
    private static void WriteIcon(string path, byte[][] images)
    {
        using var stream = File.Create(path); using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)Sizes.Length);
        var offset = 6 + Sizes.Length * 16;
        for (var i = 0; i < Sizes.Length; i++)
        {
            writer.Write((byte)(Sizes[i] == 256 ? 0 : Sizes[i])); writer.Write((byte)(Sizes[i] == 256 ? 0 : Sizes[i]));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(images[i].Length); writer.Write(offset); offset += images[i].Length;
        }
        foreach (var image in images) writer.Write(image);
    }
}
