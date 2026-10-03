using System.Globalization;
using System.Windows.Media;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.App.ViewModels;

/// <summary>Appearance editor draft; invalid partial text never reaches the desktop/model.</summary>
public sealed class FenceAppearanceViewModel(Action<string, FenceAppearance> preview) : ObservableObject
{
    private bool _loading;
    private string _name = "", _fontFamily = FenceAppearance.Default.FontFamily, _fontSize = "16";
    private bool _bold, _italic;
    private FenceTitleAlignment _alignment;
    private string _textColor = "#FFFFFF", _headerColor = "#2D415A", _bodyColor = "#182742", _borderColor = "#BED2EB", _error = "";
    private double _headerOpacity = 100, _bodyOpacity = FenceAppearance.Default.BodyOpacity, _borderOpacity = 100;
    private static readonly IReadOnlyList<string> InstalledFonts = Fonts.SystemFontFamilies.Select(f => f.Source).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
    public IReadOnlyList<string> FontFamilies => InstalledFonts.Append(FontFamily).Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
    public IReadOnlyList<FenceTitleAlignment> Alignments { get; } = Enum.GetValues<FenceTitleAlignment>();
    public string Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) Update(); } }
    public string FontFamily { get => _fontFamily; set { if (value is not null && SetProperty(ref _fontFamily, value)) Update(); } }
    public string FontSize { get => _fontSize; set { if (SetProperty(ref _fontSize, value)) Update(); } }
    public bool Bold { get => _bold; set { if (SetProperty(ref _bold, value)) Update(); } }
    public bool Italic { get => _italic; set { if (SetProperty(ref _italic, value)) Update(); } }
    public FenceTitleAlignment Alignment { get => _alignment; set { if (SetProperty(ref _alignment, value)) Update(); } }
    public string TextColor { get => _textColor; set { if (SetProperty(ref _textColor, value)) { Update(); OnPropertyChanged(nameof(TextPreview)); } } }
    public string HeaderColor { get => _headerColor; set { if (SetProperty(ref _headerColor, value)) { Update(); OnPropertyChanged(nameof(HeaderPreview)); } } }
    public string BodyColor { get => _bodyColor; set { if (SetProperty(ref _bodyColor, value)) { Update(); OnPropertyChanged(nameof(BodyPreview)); } } }
    public string BorderColor { get => _borderColor; set { if (SetProperty(ref _borderColor, value)) { Update(); OnPropertyChanged(nameof(BorderPreview)); } } }
    public double HeaderOpacity { get => _headerOpacity; set { if (SetProperty(ref _headerOpacity, value)) Update(); } }
    public double BodyOpacity { get => _bodyOpacity; set { if (SetProperty(ref _bodyOpacity, value)) Update(); } }
    public double BorderOpacity { get => _borderOpacity; set { if (SetProperty(ref _borderOpacity, value)) Update(); } }
    public string TextPreview => PreviewColor(TextColor);
    public string HeaderPreview => PreviewColor(HeaderColor);
    public string BodyPreview => PreviewColor(BodyColor);
    public string BorderPreview => PreviewColor(BorderColor);
    private static string PreviewColor(string value)
    {
        try { return FenceAppearance.NormalizeColor(value); } catch (ArgumentException) { return "#292331"; }
    }
    public void Load(Fence? fence)
    {
        _loading = true;
        try
        {
            var a = fence?.Appearance ?? FenceAppearance.Default;
            _name = fence?.Name ?? ""; _fontFamily = a.FontFamily; _fontSize = a.FontSize.ToString("G", CultureInfo.CurrentCulture);
            _bold = a.Bold; _italic = a.Italic; _alignment = a.Alignment;
            _textColor = a.TextColor; _headerColor = a.HeaderColor; _bodyColor = a.BodyColor; _borderColor = a.BorderColor;
            _headerOpacity = a.HeaderOpacity; _bodyOpacity = a.BodyOpacity; _borderOpacity = a.BorderOpacity; _error = "";
            OnPropertyChanged(string.Empty);
        }
        finally { _loading = false; }
    }
    private void Update()
    {
        if (_loading) return;
        try
        {
            if (!(double.TryParse(FontSize, NumberStyles.Float, CultureInfo.CurrentCulture, out var size) ||
                double.TryParse(FontSize, NumberStyles.Float, CultureInfo.InvariantCulture, out size)))
                throw new ArgumentException("Размер шрифта: введите число от 8 до 28.");
            var appearance = new FenceAppearance(FontFamily, size, Bold, Italic, Alignment, TextColor,
                HeaderColor, HeaderOpacity, BodyColor, BodyOpacity, BorderColor, BorderOpacity);
            preview(Name, appearance); Error = "";
        }
        catch (ArgumentException error) { Error = error.Message; }
    }
}
