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
}
