using System.IO;
using System.Windows;
using System.Windows.Controls;
using DesktopOrganizer.App.ViewModels;
using DesktopOrganizer.Core.Localization;
namespace DesktopOrganizer.App.Views;
public partial class FencesView : UserControl
{
    public FencesView() => InitializeComponent();

    private void OnMoveSharedShortcuts(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel model || !model.HasSharedShortcuts) return;
        var paths = model.SharedShortcutPaths;
        var prompt = Texts.F("Перенести эти ярлыки на ваш личный рабочий стол? Они исчезнут с рабочих столов других учётных записей. Привязки к группам сохранятся.\n\n{0}", string.Join("\n", paths.Select(Path.GetFileName)));
        if (MessageBox.Show(Window.GetWindow(this), prompt, Texts.T("Исправить общие ярлыки"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            model.MoveSharedShortcuts(paths);
    }
}
