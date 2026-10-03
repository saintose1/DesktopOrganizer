using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.App.Desktop;

/// <summary>Cached header bitmap. Body/frame stay in the existing native layered surfaces.</summary>
public sealed class FenceBitmapRenderer
{
    private readonly Dictionary<Guid, Cache> _cache = [];
    public long HeaderRasterizations { get; private set; }
    private static Brush Brush(string hex, double opacity = 100, byte brighten = 0)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        color.A = (byte)Math.Round(opacity * 255 / 100);
        color.R = (byte)Math.Min(255, color.R + brighten);
        color.G = (byte)Math.Min(255, color.G + brighten);
        color.B = (byte)Math.Min(255, color.B + brighten);
        var brush = new SolidColorBrush(color); brush.Freeze(); return brush;
    }
    public byte[] Render(Fence fence, FenceSurfaceLayout layout, double scale, FenceHit hover = FenceHit.None)
    {
        var height = Math.Max(1, Math.Min(layout.Height, (int)Math.Ceiling(fence.Y + FenceInteractionGeometry.HeaderHeight * scale - layout.Y)));
        if ((long)layout.Width * height > 4_194_304) throw new InvalidOperationException("Fence header exceeds bitmap safety limit.");
        var appearance = fence.Appearance;
        var key = (fence.Name, appearance, layout.Width, height, fence.Width, fence.X - layout.X, fence.Y - layout.Y, scale, fence.IsCollapsed, hover);
        if (!_cache.TryGetValue(fence.Id, out var entry)) { entry = new Cache(); _cache.Add(fence.Id, entry); }
        if (entry.Key == key && entry.Pixels is not null) return entry.Pixels;
        if (entry.Bitmap is null || entry.Bitmap.PixelWidth != layout.Width || entry.Bitmap.PixelHeight != height)
            entry.Bitmap = new RenderTargetBitmap(layout.Width, height, 96, 96, PixelFormats.Pbgra32);
        else entry.Bitmap.Clear();
        var header = new FenceHeaderLayout(fence.Width, scale);
        if (entry.Text is null || entry.Name != fence.Name || entry.Appearance != appearance || entry.Scale != scale)
        {
        entry.Text = new FormattedText(fence.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(appearance.FontFamily), appearance.Italic ? FontStyles.Italic : FontStyles.Normal,
                appearance.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            appearance.FontSize * scale, Brush(appearance.TextColor), 1)
        {
            Trimming = TextTrimming.CharacterEllipsis,
            MaxTextWidth = Math.Max(1, header.TitleWidth),
            TextAlignment = appearance.Alignment switch { FenceTitleAlignment.Center => TextAlignment.Center, FenceTitleAlignment.Right => TextAlignment.Right, _ => TextAlignment.Left }
        };
        entry.Name = fence.Name; entry.Appearance = appearance; entry.Scale = scale;
        }
        var text = entry.Text;
        text.MaxTextWidth = Math.Max(1, header.TitleWidth);
        using (var drawing = entry.Visual.RenderOpen())
        {
            // Keep native header input even at 0% opacity; alpha 1/255 is visually negligible.
            var opacity = Math.Max(100.0 / 255, appearance.HeaderOpacity);
            drawing.DrawRectangle(Brush(appearance.HeaderColor, opacity, hover == FenceHit.Header ? (byte)15 : (byte)0), null,
                new Rect(0, 0, layout.Width, height));
            var headerY = Math.Clamp(fence.Y - layout.Y, -height, 0);
            foreach (var action in header.Actions)
            {
                var x = fence.X + action.X - layout.X;
                if (x < -action.Width || x > layout.Width) continue;
                drawing.DrawRoundedRectangle(Brush(appearance.HeaderColor, opacity, hover == FenceHit.Collapse ? (byte)45 : (byte)22), null,
                    new Rect(x, headerY + action.Y, action.Width, action.Height), 3, 3);
                var center = new Point(x + action.Width / 2, headerY + header.Height / 2);
                var pen = new Pen(Brush(appearance.TextColor), 1.5 * scale);
                var direction = fence.IsCollapsed ? 1 : -1;
                drawing.DrawLine(pen, new(center.X - 5 * scale, center.Y - direction * 2 * scale), new(center.X, center.Y + direction * 3 * scale));
                drawing.DrawLine(pen, new(center.X, center.Y + direction * 3 * scale), new(center.X + 5 * scale, center.Y - direction * 2 * scale));
            }
            var titleX = fence.X - layout.X + 12 * scale;
            drawing.PushClip(new RectangleGeometry(new Rect(titleX, headerY, header.TitleWidth, header.Height)));
            drawing.DrawText(text, new(titleX, headerY + Math.Max(0, (header.Height - text.Height) / 2)));
            drawing.Pop();
        }
        entry.Bitmap.Render(entry.Visual);
        HeaderRasterizations++;
        var size = checked(layout.Width * height * 4);
        if (entry.Pixels is null || entry.Pixels.Length < size)
            entry.Pixels = new byte[Math.Max(size, entry.Pixels?.Length * 2 ?? size)];
        entry.Bitmap.CopyPixels(entry.Pixels, layout.Width * 4, 0);
        entry.Key = key;
        return entry.Pixels;
    }
    public void Remove(Guid id) => _cache.Remove(id);
    public void Clear() => _cache.Clear();
    private sealed class Cache
    {
        public DrawingVisual Visual { get; } = new();
        public RenderTargetBitmap? Bitmap;
        public FormattedText? Text;
        public string? Name;
        public FenceAppearance? Appearance;
        public double Scale;
        public byte[]? Pixels;
        public (string Name, FenceAppearance Appearance, int Width, int Height, double ModelWidth, double OffsetX, double OffsetY, double Scale, bool Collapsed, FenceHit Hover) Key;
    }
}
