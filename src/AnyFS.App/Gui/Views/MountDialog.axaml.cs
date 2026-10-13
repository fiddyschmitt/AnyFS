using AnyFS.App.Gui.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AnyFS.App.Gui.Views;

public partial class MountDialog : Window
{
    public MountDialog(MountDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Opened += (_, _) => NameBox.Focus();
    }

    private async void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MountDialogViewModel vm && await vm.TryAcceptAsync())
        {
            Close(true);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
