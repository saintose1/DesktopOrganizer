using System.Windows;
using DesktopOrganizer.App.ViewModels;

namespace DesktopOrganizer.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
    private void CloseWindow(object sender, RoutedEventArgs args) => Close();
    private void MinimizeWindow(object sender, RoutedEventArgs args) => SystemCommands.MinimizeWindow(this);
    private void ToggleMaximizeWindow(object sender, RoutedEventArgs args)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
}
