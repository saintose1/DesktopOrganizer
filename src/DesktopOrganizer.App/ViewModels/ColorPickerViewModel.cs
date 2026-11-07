using DesktopOrganizer.Core.Localization;
using System.Globalization;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.App.ViewModels;

public sealed class ColorPickerViewModel : ObservableObject
{
    private double _hue, _saturation, _value = 1;
    private string _color = "#FFFFFF", _hex = "#FFFFFF", _error = "";
    public event Action<string>? ColorChanged;
    public string Color => _color;
    public string HueColor => HsvColor.ToHex(Hue, 1, 1);
    public string Error => _error;
    public string Hex
    {
        get => _hex;
        set
        {
            if (!SetProperty(ref _hex, value)) return;
            try { LoadColor(FenceAppearance.NormalizeColor(value)); ColorChanged?.Invoke(Color); }
            catch (ArgumentException) { _error = Texts.T("Используйте #RRGGBB"); OnPropertyChanged(nameof(Error)); }
        }
    }
    public double Hue
    {
        get => _hue;
        set { if (double.IsFinite(value) && SetProperty(ref _hue, Math.Clamp(value, 0, 360))) { OnPropertyChanged(nameof(HueColor)); Publish(); } }
    }
    public double Saturation => _saturation;
    public double Value => _value;
    public IReadOnlyList<string> Presets { get; } = ["#FFFFFF", "#CDAAFF", "#AD8FE8", "#553366", "#372342", "#182742", "#65D1B3", "#FFADCF", "#FFD38B", "#000000"];
    public void SetSaturationValue(double saturation, double value)
    {
        if (!double.IsFinite(saturation) || !double.IsFinite(value)) return;
        _saturation = Math.Clamp(saturation, 0, 1); _value = Math.Clamp(value, 0, 1);
        OnPropertyChanged(nameof(Saturation)); OnPropertyChanged(nameof(Value)); Publish();
    }
    public void SelectPreset(string color) { LoadColor(color); ColorChanged?.Invoke(Color); }
    public void LoadColor(string color)
    {
        color = FenceAppearance.NormalizeColor(color);
        if (color != _color)
        {
            (_hue, _saturation, _value) = HsvColor.FromHex(color);
            _color = color;
            OnPropertyChanged(nameof(Hue)); OnPropertyChanged(nameof(Saturation)); OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(Color)); OnPropertyChanged(nameof(HueColor));
        }
        _hex = color; _error = ""; OnPropertyChanged(nameof(Hex)); OnPropertyChanged(nameof(Error));
    }
    private void Publish()
    {
        _color = HsvColor.ToHex(Hue, Saturation, Value); _hex = _color; _error = "";
        OnPropertyChanged(nameof(Color)); OnPropertyChanged(nameof(Hex)); OnPropertyChanged(nameof(Error));
        ColorChanged?.Invoke(_color);
    }
}

public static class HsvColor
{
    public static string ToHex(double hue, double saturation, double value)
    {
        if (!double.IsFinite(hue) || !double.IsFinite(saturation) || !double.IsFinite(value)) throw new ArgumentException("Non-finite HSV color");
        var h = ((hue % 360) + 360) % 360 / 60;
        var c = Math.Clamp(value, 0, 1) * Math.Clamp(saturation, 0, 1);
        var x = c * (1 - Math.Abs(h % 2 - 1)); var m = Math.Clamp(value, 0, 1) - c;
        (double r, double g, double b) = h switch
        {
            < 1 => (c, x, 0.0), < 2 => (x, c, 0.0), < 3 => (0.0, c, x),
            < 4 => (0.0, x, c), < 5 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return $"#{(int)Math.Round((r + m) * 255):X2}{(int)Math.Round((g + m) * 255):X2}{(int)Math.Round((b + m) * 255):X2}";
    }
    public static (double Hue, double Saturation, double Value) FromHex(string color)
    {
        color = FenceAppearance.NormalizeColor(color);
        var r = int.Parse(color.AsSpan(1, 2), NumberStyles.HexNumber) / 255.0;
        var g = int.Parse(color.AsSpan(3, 2), NumberStyles.HexNumber) / 255.0;
        var b = int.Parse(color.AsSpan(5, 2), NumberStyles.HexNumber) / 255.0;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b)); var delta = max - min;
        var hue = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        return (hue, max == 0 ? 0 : delta / max, max);
    }
}
