using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>
/// Forest branding: a vector fern frond. application.ico — frond on a dark moss tile (EXE, window, taskbar,
/// desktop shortcut, installer); tray.ico — the frond alone, bolder, readable on light and dark taskbars.
/// Small sizes use fewer, larger leaflets so the shape survives 16 px.
/// </summary>
internal static class Program
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 48, 64, 128, 256];
    private static readonly Color Moss = Color.FromRgb(0x13, 0x24, 0x19), MossEdge = Color.FromRgb(0x2E, 0x4A, 0x33);
    private static readonly Color Sage = Color.FromRgb(0x86, 0xA3, 0x5F), SageLight = Color.FromRgb(0xB8, 0xCC, 0x86);
    private static readonly Color Leaf = Color.FromRgb(0x7F, 0xA8, 0x4E), LeafLight = Color.FromRgb(0xA9, 0xC8, 0x72), Stem = Color.FromRgb(0x5E, 0x7F, 0x3A);

    [STAThread]
    private static void Main(string[] args)
    {
        var directory = Path.GetFullPath(args.Length == 1 ? args[0] : "src/DesktopOrganizer.App/Assets/Branding");
        var main = Sizes.Select(size => Render(size, dc => DrawApplication(dc, size))).ToArray();
        var tray = Sizes.Select(size => Render(size, dc => DrawTray(dc, size))).ToArray();
        WriteIcon(Path.Combine(directory, "application.ico"), main);
        WriteIcon(Path.Combine(directory, "tray.ico"), tray);
        File.WriteAllBytes(Path.Combine(directory, "tray-preview.png"), tray[5]);
        File.WriteAllBytes(Path.Combine(directory, "application-preview.png"), main[^1]);
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

    private static void DrawApplication(DrawingContext dc, int size)
    {
        var tile = new Rect(2, 2, 60, 60);
        var radius = size <= 24 ? 9 : 13;
        dc.DrawRoundedRectangle(new SolidColorBrush(Moss), new Pen(new SolidColorBrush(MossEdge), size <= 24 ? 3 : 2), tile, radius, radius);
        // Faint moss mound the frond grows from.
        if (size >= 32)
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x55, 0x4F, 0x73, 0x40)), null,
                Geometry.Parse("M8,58 Q16,47 28,50 Q36,44 46,49 Q55,47 58,58 Z"));
        if (size <= 24)
            DrawFrond(dc, size, new Point(17, 56), new Point(28, 28), new Point(49, 7), 3, 24, new SolidColorBrush(Sage),
                new SolidColorBrush(SageLight), new SolidColorBrush(SageLight), 4.5);
        else
            DrawFrond(dc, size, new Point(19, 54), new Point(29, 26), new Point(47, 9), size <= 48 ? 6 : 9, 15,
                new SolidColorBrush(Sage), new SolidColorBrush(SageLight), new SolidColorBrush(SageLight), 2.2);
    }

    private static void DrawTray(DrawingContext dc, int size)
    {
        // No tile: the frond itself fills the square; mid green reads on both light and dark taskbars.
        if (size <= 24)
            DrawFrond(dc, size, new Point(11, 62), new Point(25, 30), new Point(55, 2), 3, 30, new SolidColorBrush(Leaf),
                new SolidColorBrush(LeafLight), new SolidColorBrush(Stem), 6.5);
        else
            DrawFrond(dc, size, new Point(14, 61), new Point(26, 28), new Point(52, 3), size <= 48 ? 6 : 9, 20,
                new SolidColorBrush(Leaf), new SolidColorBrush(LeafLight), new SolidColorBrush(Stem), 3.4);
    }

    /// <summary>A frond along a quadratic stem: pairs of pointed leaflets that shrink toward the tip.</summary>
    private static void DrawFrond(DrawingContext dc, int size, Point start, Point control, Point end, int pairs, double maxLength,
        Brush leaf, Brush tipLeaf, Brush stem, double stemWidth)
    {
        Point At(double t) => new((1 - t) * (1 - t) * start.X + 2 * (1 - t) * t * control.X + t * t * end.X,
                                  (1 - t) * (1 - t) * start.Y + 2 * (1 - t) * t * control.Y + t * t * end.Y);
        Vector Tangent(double t)
        {
            var v = new Vector(2 * (1 - t) * (control.X - start.X) + 2 * t * (end.X - control.X),
                               2 * (1 - t) * (control.Y - start.Y) + 2 * t * (end.Y - control.Y));
            v.Normalize(); return v;
        }
        var stemGeometry = new PathGeometry([new PathFigure(start, [new QuadraticBezierSegment(control, end, true)], false)]);
        dc.DrawGeometry(null, new Pen(stem, stemWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, stemGeometry);
        for (var i = 0; i < pairs; i++)
        {
            var t = 0.16 + i * (0.74 / pairs);
            var point = At(t);
            var tangent = Tangent(t);
            var length = maxLength * (1 - 0.62 * t);
            var width = length * (size <= 24 ? 0.5 : 0.36);
            var brush = t > 0.62 ? tipLeaf : leaf;
            foreach (var side in new[] { -1, 1 })
            {
                // Leaflets point forward along the stem at ~58° from it.
                var angle = Math.Atan2(tangent.Y, tangent.X) * 180 / Math.PI + side * 58;
                var shape = Geometry.Parse(FormattableString.Invariant($"M0,0 Q{length * 0.45},{-width} {length},0 Q{length * 0.45},{width} 0,0 Z")).Clone();
                var transform = new TransformGroup();
                transform.Children.Add(new RotateTransform(angle));
                transform.Children.Add(new TranslateTransform(point.X, point.Y));
                shape.Transform = transform;
                dc.DrawGeometry(brush, null, shape);
            }
        }
        // Terminal leaflet.
        var tip = Tangent(1);
        var tipAngle = Math.Atan2(tip.Y, tip.X) * 180 / Math.PI;
        var tipLength = maxLength * 0.42;
        var tipShape = Geometry.Parse(FormattableString.Invariant($"M0,0 Q{tipLength * 0.45},{-tipLength * 0.38} {tipLength},0 Q{tipLength * 0.45},{tipLength * 0.38} 0,0 Z")).Clone();
        var group = new TransformGroup();
        group.Children.Add(new RotateTransform(tipAngle));
        group.Children.Add(new TranslateTransform(At(0.9).X, At(0.9).Y));
        tipShape.Transform = group;
        dc.DrawGeometry(tipLeaf, null, tipShape);
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
