using DesktopOrganizer.Core.Localization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DesktopOrganizer.Infrastructure.Settings;

/// <summary>One current-user Run value. Never modifies other applications or machine-wide settings.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupRegistration(string executablePath, ILogger<WindowsStartupRegistration> logger) : IStartupRegistration
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "DesktopOrganizer";
    public string Command { get; } = CreateCommand(executablePath);
    public static string CreateCommand(string executablePath)
    {
        if (!Path.IsPathFullyQualified(executablePath) || executablePath.Contains('"') ||
            executablePath.Any(char.IsControl) || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(Texts.T("Требуется абсолютный путь к исполняемому файлу приложения."), nameof(executablePath));
        var command = $"\"{Path.GetFullPath(executablePath)}\" --StartInTray=true";
        if (command.Length > 260) throw new ArgumentException(Texts.T("Путь слишком длинный для Windows Run. Переместите приложение в папку с более коротким путём."), nameof(executablePath));
        return command;
    }
    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
    }
    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            var path = Command[1..Command.IndexOf('"', 1)];
            if (!File.Exists(path)) throw new FileNotFoundException(Texts.T("Исполняемый файл приложения не найден."), path);
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        if (IsEnabled() != enabled) throw new IOException(Texts.T("Windows не сохранила настройку автозапуска."));
        logger.LogInformation("Current-user autostart {State}", enabled ? "enabled" : "disabled");
    }
}
