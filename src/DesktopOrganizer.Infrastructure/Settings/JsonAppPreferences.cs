using DesktopOrganizer.Core.Localization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.Infrastructure.Settings;

public sealed record AppPreferences
{
    public const double MaximumAnimationSeconds = 3;
    public bool AutoExpandOnHover { get; }
    public double ExpandAnimationSeconds { get; }
    public double CollapseAnimationSeconds { get; }
    /// <summary>"ru", "en", or null to follow the Windows display language (files from older versions).</summary>
    public string? Language { get; }
    public AppPreferences(bool autoExpandOnHover = false, double expandAnimationSeconds = 0.18, double collapseAnimationSeconds = 0.14,
        string? language = null)
    {
        ValidateSeconds(expandAnimationSeconds); ValidateSeconds(collapseAnimationSeconds);
        AutoExpandOnHover = autoExpandOnHover;
        ExpandAnimationSeconds = expandAnimationSeconds;
        CollapseAnimationSeconds = collapseAnimationSeconds;
        Language = language is Texts.Russian or Texts.English ? language : null;
    }
    private static void ValidateSeconds(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > MaximumAnimationSeconds)
            throw new ArgumentException(Texts.T("Длительность должна быть от 0 до 3 секунд."));
    }
}

public interface IAppPreferencesStore
{
    AppPreferences Load();
    void Save(AppPreferences preferences);
}

/// <summary>Global preferences, independent of Fence documents and Windows autostart.</summary>
public sealed class JsonAppPreferences(string directory, ILogger<JsonAppPreferences> logger) : IAppPreferencesStore
{
    private readonly string _path = Path.Combine(directory, "settings.json");
    private bool _readFailed;
    public AppPreferences Load()
    {
        try
        {
            var result = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(_path)) ?? throw new JsonException("Empty preferences document");
            _readFailed = false;
            return result;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { _readFailed = false; return new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            _readFailed = true;
            logger.LogError(error, "Could not read application preferences; original document retained");
            throw;
        }
    }
    public void Save(AppPreferences preferences)
    {
        if (_readFailed) throw new IOException(Texts.T("Настройки недоступны или повреждены. Исходный settings.json сохранён; исправьте файл и откройте настройки снова."));
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, preferences);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Could not save application preferences");
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
