using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DesktopOrganizer.App.ViewModels;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.App.Views;

/// <summary>View-only popup/capture/coordinate handling. HSV and color state belong to the ViewModel.</summary>
public partial class ColorPickerField : UserControl
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(string), typeof(ColorPickerField),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnColorChanged));
    public string Color { get => (string)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public ColorPickerViewModel Picker { get; } = new();
    public ColorPickerField()
    {
        InitializeComponent();
        Picker.ColorChanged += color => SetCurrentValue(ColorProperty, color);
    }
    private static void OnColorChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var field = (ColorPickerField)sender;
        try { field.Picker.LoadColor((string)args.NewValue); }
        catch (ArgumentException) { /* Partial external HEX drafts do not replace a valid preview. */ }
    }
    private void OnOpen(object sender, RoutedEventArgs args) => Palette.IsOpen = !Palette.IsOpen;
    private void OnPopupOpened(object? sender, EventArgs args)
    {
        try { Picker.LoadColor(FenceAppearance.NormalizeColor(Color)); }
        catch (ArgumentException) { }
    }
    private void OnPopupClosed(object? sender, EventArgs args) { if (Spectrum.IsMouseCaptured) Spectrum.ReleaseMouseCapture(); }
    private void OnSpectrumDown(object sender, MouseButtonEventArgs args) { Spectrum.CaptureMouse(); Pick(args); args.Handled = true; }
    private void OnSpectrumMove(object sender, MouseEventArgs args) { if (Spectrum.IsMouseCaptured && args.LeftButton == MouseButtonState.Pressed) Pick(args); }
    private void OnSpectrumUp(object sender, MouseButtonEventArgs args) { if (Spectrum.IsMouseCaptured) { Pick(args); Spectrum.ReleaseMouseCapture(); args.Handled = true; } }
    private void Pick(MouseEventArgs args)
    {
        if (Spectrum.ActualWidth <= 0 || Spectrum.ActualHeight <= 0) return;
        var point = args.GetPosition(Spectrum);
        Picker.SetSaturationValue(point.X / Spectrum.ActualWidth, 1 - point.Y / Spectrum.ActualHeight);
    }
    private void OnPreset(object sender, RoutedEventArgs args) => Picker.SelectPreset((string)((Button)sender).Tag);
}

public sealed class ColorCursorOffsetConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2 || values[0] is not double fraction || values[1] is not double extent) return 0.0;
        return (parameter is "Invert" ? 1 - fraction : fraction) * extent - 5;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
