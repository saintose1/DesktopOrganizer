namespace DesktopOrganizer.Infrastructure.Settings;

public interface IStartupRegistration
{
    bool IsEnabled();
    void SetEnabled(bool enabled);
}
