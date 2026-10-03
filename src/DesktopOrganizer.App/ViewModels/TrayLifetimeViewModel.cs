namespace DesktopOrganizer.App.ViewModels;

/// <summary>Window lifetime policy, independent of native callbacks and WPF events.</summary>
public sealed class TrayLifetimeViewModel(Action show, Action hide, Func<Task> prepareExit, Action shutdown, Action<Exception> reportError)
{
    public bool IsExiting { get; private set; }
    public bool IsPreparingExit { get; private set; }
    public void Open() { if (!IsExiting) show(); }
    public bool RequestWindowClose()
    {
        if (IsExiting) return true;
        hide();
        return false;
    }
    public async Task ExitAsync()
    {
        if (IsExiting || IsPreparingExit) return;
        IsPreparingExit = true;
        try
        {
            await prepareExit();
            IsExiting = true;
            shutdown();
        }
        catch (Exception error) { show(); reportError(error); }
        finally { IsPreparingExit = false; }
    }
    // Windows session termination must bypass hide-on-close, even before tray startup.
    public void AllowSessionExit() => IsExiting = true;
}
