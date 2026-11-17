using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DesktopOrganizer.App.Views;

/// <summary>"#RRGGBB" + opacity 0..100 (as in the group editor) to a brush; invalid input gives a transparent brush.</summary>
public sealed class HexOpacityBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(values[0]?.ToString() ?? "#000000");
            var opacity = values.Length > 1 && values[1] is double percent ? Math.Clamp(percent, 0, 100) : 100;
            color.A = (byte)Math.Round(opacity * 255 / 100);
            return new SolidColorBrush(color);
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or NullReferenceException)
        {
            return Brushes.Transparent;
        }
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Brush from a plain "#RRGGBB" string (opacity 100%).</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value?.ToString() ?? "#000000")); }
        catch (Exception error) when (error is FormatException or InvalidCastException) { return Brushes.Transparent; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
