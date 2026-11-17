using System.Windows.Input;

namespace DesktopOrganizer.App.Commands;

/// <summary>Command that receives its CommandParameter (a group id, a rule row).</summary>
public sealed class ParameterCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(parameter); }
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
