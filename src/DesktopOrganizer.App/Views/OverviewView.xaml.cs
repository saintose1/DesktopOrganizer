using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopOrganizer.App.ViewModels;

namespace DesktopOrganizer.App.Views;

public partial class OverviewView : UserControl
{
    public OverviewView() => InitializeComponent();

    /// <summary>A click on a group card opens that group on the Groups page.</summary>
    private void OnCardClick(object sender, MouseButtonEventArgs args)
    {
        if (sender is FrameworkElement { Tag: Guid id } && DataContext is MainWindowViewModel viewModel) viewModel.OpenFenceCommand.Execute(id);
    }

    /// <summary>The new group is shown where it can be edited.</summary>
    private void OnCreate(object sender, RoutedEventArgs args)
    {
        if (DataContext is MainWindowViewModel viewModel && viewModel.CreateCommand.CanExecute(null)) viewModel.OpenFencesCommand.Execute(null);
    }
}
