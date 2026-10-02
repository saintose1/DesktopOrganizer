using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.App.Desktop;

/// <summary>Only the small header bitmap is rasterized by WPF. Native GDI owns body/frame.</summary>
public sealed class FenceBitmapRenderer
{
    private readonly Dictionary<Guid, Cache> _cache = [];
    private static readonly Brush Header = Frozen(Color.FromRgb(45, 65, 90));
    private static readonly Brush HeaderHover = Frozen(Color.FromRgb(60, 83, 111));
    private static readonly Brush Button = Frozen(Color.FromRgb(65, 87, 113));
    private static readonly Brush ButtonHover = Frozen(Color.FromRgb(91, 125, 161));
    private static readonly Typeface Typeface = new("Segoe UI Semibold");
    private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    public byte[] Render(Fence fence, FenceSurfaceLayout layout, double scale, FenceHit hover = FenceHit.None)
    {
        var height = Math.Max(1, Math.Min(layout.Height, (int)Math.Ceiling(fence.Y + FenceInteractionGeometry.HeaderHeight * scale - layout.Y)));
        if ((long)layout.Width * height > 4_194_304) throw new InvalidOperationException("Fence header exceeds bitmap safety limit.");
        var key = (fence.Name, layout.Width, height, fence.Width, fence.X - layout.X, fence.Y - layout.Y, scale, fence.IsCollapsed, hover);
        if (!_cache.TryGetValue(fence.Id, out var entry)) { entry = new Cache(); _cache.Add(fence.Id, entry); }
        if (entry.Key == key && entry.Pixels is not null) return entry.Pixels;
        if (entry.Bitmap is null || entry.Bitmap.PixelWidth != layout.Width || entry.Bitmap.PixelHeight != height)
            entry.Bitmap = new RenderTargetBitmap(layout.Width, height, 96, 96, PixelFormats.Pbgra32);
        else entry.Bitmap.Clear();
        if (entry.Text is null || entry.Name != fence.Name || entry.Scale != scale)
        {
            entry.Text = new FormattedText(fence.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface, 16 * scale, Brushes.White, 1)
                { Trimming = TextTrimming.CharacterEllipsis };
            entry.Name = fence.Name; entry.Scale = scale;
        }
        var header = new FenceHeaderLayout(fence.Width, scale);
        entry.Text.MaxTextWidth = Math.Max(1, Math.Min(layout.Width - 24 * scale, header.TitleWidth));
        entry.Text.MaxTextHeight = Math.Max(1, height);
        using (var drawing = entry.Visual.RenderOpen())
        {
            drawing.DrawRectangle(hover == FenceHit.Header ? HeaderHover : Header, null, new Rect(0, 0, layout.Width, height));
            var headerY = Math.Clamp(fence.Y - layout.Y, -height, 0);
            foreach (var action in header.Actions)
            {
                var x = fence.X + action.X - layout.X;
                if (x < -action.Width || x > layout.Width) continue;
                drawing.DrawRoundedRectangle(hover == FenceHit.Collapse ? ButtonHover : Button, null,
                    new Rect(x, headerY + action.Y, action.Width, action.Height), 3, 3);
                var center = new Point(x + action.Width / 2, headerY + header.Height / 2);
                var pen = new Pen(Brushes.White, 1.5 * scale);
                var direction = fence.IsCollapsed ? 1 : -1;
                drawing.DrawLine(pen, new(center.X - 5 * scale, center.Y - direction * 2 * scale), new(center.X, center.Y + direction * 3 * scale));
                drawing.DrawLine(pen, new(center.X, center.Y + direction * 3 * scale), new(center.X + 5 * scale, center.Y - direction * 2 * scale));
            }
            drawing.DrawText(entry.Text, new(Math.Clamp(fence.X - layout.X, -layout.Width, 0) + 12 * scale, headerY + 7 * scale));
        }
        entry.Bitmap.Render(entry.Visual);
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
        public double Scale;
        public byte[]? Pixels;
        public (string Name, int Width, int Height, double ModelWidth, double OffsetX, double OffsetY, double Scale, bool Collapsed, FenceHit Hover) Key;
    }
}
