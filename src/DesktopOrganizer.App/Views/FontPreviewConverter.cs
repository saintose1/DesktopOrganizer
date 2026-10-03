using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DesktopOrganizer.App.Views;

/// <summary>Evaluated only for realized ComboBox entries; missing glyphs fall back per entry.</summary>
public sealed class FontPreviewConverter : IValueConverter
{
    private static readonly FontFamily Fallback = new("Segoe UI");
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string name) return Fallback;
        try
        {
            var family = new FontFamily(name);
            var face = new Typeface(family, System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
            return face.TryGetGlyphTypeface(out var glyphs) && name.All(c => char.IsWhiteSpace(c) || glyphs.CharacterToGlyphMap.ContainsKey(c)) ? family : Fallback;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.IOException) { return Fallback; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
