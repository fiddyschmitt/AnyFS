using AnyFS.App.Gui.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AnyFS.App.Gui.Views;

public partial class ExistingSourceDialog : Window
{
    public ExistingSourceDialog(ExistingSourceDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Opened += (_, _) =>
        {
            PathBox.Focus();
            PathBox.SelectAll();
        };
    }

    private async void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ExistingSourceDialogViewModel vm && await vm.TryAcceptAsync())
        {
            Close(true);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
