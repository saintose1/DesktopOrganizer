using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using DesktopOrganizer.App.ViewModels;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Infrastructure.Interop;

namespace DesktopOrganizer.App.Views;

public partial class ProgramSearchWindow : Window
{
    private readonly ProgramSearchViewModel _model;
    private bool _closing;

    public ProgramSearchWindow(ProgramSearchViewModel model, DesktopPoint anchor)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        model.Opened += Dismiss;
        Loaded += async (_, _) =>
        {
            DesktopShell.PlacePopup(new WindowInteropHelper(this).Handle, anchor);
            SearchBox.Focus();
            await model.LoadAsync();
        };
        Closing += (_, _) => _closing = true;
        Closed += (_, _) => model.Opened -= Dismiss;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Dismiss(); }
        else if (e.Key == Key.Enter && _model.OpenCommand.CanExecute(null))
        { e.Handled = true; _model.OpenCommand.Execute(null); }
        else if (e.Key == Key.Down && SearchBox.IsKeyboardFocusWithin && _model.Selected is not null)
        {
            ResultsList.ScrollIntoView(_model.Selected);
            if (ResultsList.ItemContainerGenerator.ContainerFromItem(_model.Selected) is ListBoxItem selected) selected.Focus();
            e.Handled = true;
        }
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ResultsList, source) is ListBoxItem &&
            _model.OpenCommand.CanExecute(null)) _model.OpenCommand.Execute(null);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (IsLoaded) Dismiss();
    }

    private void Dismiss()
    {
        if (!_closing) Close();
    }
}
