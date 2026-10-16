using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NovaTune.App.ViewModels;

namespace NovaTune.App.Views;

public partial class ToolsView : UserControl
{
    public ToolsView() => InitializeComponent();

    // Тест реакции считается по нажатию кнопки мыши (PreviewMouseDown), а не по Click (отпусканию).
    private void OnReactionDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ToolsViewModel vm)
        {
            vm.ReactionPress();
            e.Handled = true;
        }
    }

    private void OnReactionReset(object sender, RoutedEventArgs e) => (DataContext as ToolsViewModel)?.ResetReaction();
}
