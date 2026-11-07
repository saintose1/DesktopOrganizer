using DesktopOrganizer.Core.Localization;
using System.Globalization;

namespace DesktopOrganizer.Core.Models;

public enum FenceTitleAlignment { Left, Center, Right }

/// <summary>Validated, platform-independent visual settings. Opacity is a percentage.</summary>
public sealed record FenceAppearance
{
    public static FenceAppearance Default { get; } = new();
    public string FontFamily { get; }
    public double FontSize { get; }
    public bool Bold { get; }
    public bool Italic { get; }
    public FenceTitleAlignment Alignment { get; }
    public string TextColor { get; }
    public string HeaderColor { get; }
    public double HeaderOpacity { get; }
    public string BodyColor { get; }
    public double BodyOpacity { get; }
    public string BorderColor { get; }
    public double BorderOpacity { get; }

    public FenceAppearance(string fontFamily = "Segoe UI Semibold", double fontSize = 16,
        bool bold = false, bool italic = false, FenceTitleAlignment alignment = FenceTitleAlignment.Left,
        string textColor = "#FFFFFF", string headerColor = "#2D415A", double headerOpacity = 100,
        string bodyColor = "#182742", double bodyOpacity = 41.1764705882353,
        string borderColor = "#BED2EB", double borderOpacity = 100)
    {
        if (string.IsNullOrWhiteSpace(fontFamily) || fontFamily.Length > 100 || fontFamily.Any(char.IsControl))
            throw new ArgumentException(Texts.T("Шрифт: укажите название от 1 до 100 символов."));
        if (!double.IsFinite(fontSize) || fontSize is < 8 or > 28)
            throw new ArgumentException(Texts.T("Размер шрифта: от 8 до 28. Высота заголовка фиксирована."));
        if (!Enum.IsDefined(alignment)) throw new ArgumentException(Texts.T("Неизвестное выравнивание текста."));
        FontFamily = fontFamily.Trim(); FontSize = fontSize; Bold = bold; Italic = italic; Alignment = alignment;
        TextColor = NormalizeColor(textColor); HeaderColor = NormalizeColor(headerColor);
        BodyColor = NormalizeColor(bodyColor); BorderColor = NormalizeColor(borderColor);
        HeaderOpacity = ValidateOpacity(headerOpacity); BodyOpacity = ValidateOpacity(bodyOpacity); BorderOpacity = ValidateOpacity(borderOpacity);
    }

    public static string NormalizeColor(string color)
    {
        if (color is null || color.Length != 7 || color[0] != '#' || color.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF") ||
            !uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException(Texts.T("Цвет: введите HEX в формате #RRGGBB."));
        return color.ToUpperInvariant();
    }
    private static double ValidateOpacity(double value) => double.IsFinite(value) && value is >= 0 and <= 100
        ? value : throw new ArgumentException(Texts.T("Непрозрачность должна быть от 0 до 100%."));
}
