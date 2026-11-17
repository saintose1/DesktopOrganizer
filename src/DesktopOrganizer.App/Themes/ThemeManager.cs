using System.Windows;
using Microsoft.Win32;

namespace DesktopOrganizer.App.Themes;

/// <summary>
/// Swaps the Forest palette (dark/light) in the application resources. The window reads brushes through
/// DynamicResource, so a change is visible at once. <c>null</c> follows the Windows "app mode" setting.
/// </summary>
public static class ThemeManager
{
    private static readonly Uri Dark = new("pack://application:,,,/DesktopOrganizer.App;component/Themes/ForestDark.xaml", UriKind.Absolute);
    private static readonly Uri Light = new("pack://application:,,,/DesktopOrganizer.App;component/Themes/ForestLight.xaml", UriKind.Absolute);
    private static string? _preference;
    private static bool _watching;

    public static bool IsLight { get; private set; }
    public static event Action? Changed;

    /// <summary>"dark", "light" or null (follow Windows).</summary>
    public static void Apply(string? preference)
    {
        _preference = preference;
        var light = preference switch { "light" => true, "dark" => false, _ => SystemUsesLightApps() };
        if (!_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += (_, args) =>
            {
                if (_preference is null && args.Category == UserPreferenceCategory.General)
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply(null));
            };
        }
        var resources = Application.Current?.Resources;
        if (resources is null) return;
        var dictionary = new ResourceDictionary { Source = light ? Light : Dark };
        var merged = resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(d => d.Source is { } s && (s.OriginalString == Dark.OriginalString || s.OriginalString == Light.OriginalString));
        if (existing is null) merged.Insert(0, dictionary);
        else merged[merged.IndexOf(existing)] = dictionary;
        if (IsLight == light && existing is not null) return;
        IsLight = light;
        Changed?.Invoke();
    }

    private static bool SystemUsesLightApps()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is int v && v != 0; }
        catch (Exception) { return false; }
    }
}
