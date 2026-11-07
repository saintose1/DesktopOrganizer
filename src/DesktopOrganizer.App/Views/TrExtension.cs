using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using DesktopOrganizer.Core.Localization;

namespace DesktopOrganizer.App.Views;

/// <summary>
/// <c>{l:Tr 'Русский текст'}</c>: the Russian source text, shown in the selected interface language and
/// updated live when the language changes in Settings.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension(string text) : MarkupExtension
{
    public string Text { get; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(nameof(LanguageSource.Revision))
        {
            Source = LanguageSource.Instance, Mode = BindingMode.OneWay,
            Converter = TranslateConverter.Instance, ConverterParameter = Text,
        };
        var target = serviceProvider.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
        // Style/trigger setters take the binding itself; plain CLR properties (e.g. Binding.TargetNullValue) a string.
        if (target?.TargetObject is Setter) return binding;
        if (target?.TargetProperty is not DependencyProperty) return Texts.T(Text);
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class TranslateConverter : IValueConverter
    {
        public static readonly TranslateConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Texts.T((string)parameter);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}

/// <summary>Raises a change whenever <see cref="Texts"/> switches language, so every Tr binding re-translates.</summary>
public sealed class LanguageSource : INotifyPropertyChanged
{
    public static LanguageSource Instance { get; } = new();
    private LanguageSource() => Texts.LanguageChanged += () =>
    {
        Revision++;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) PropertyChanged?.Invoke(this, new(nameof(Revision)));
        else dispatcher.BeginInvoke(() => PropertyChanged?.Invoke(this, new(nameof(Revision))));
    };
    public int Revision { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
}
