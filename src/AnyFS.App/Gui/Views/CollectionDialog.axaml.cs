using AnyFS.App.Gui.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AnyFS.App.Gui.Views;

public partial class CollectionDialog : Window
{
    public CollectionDialog(CollectionDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private async void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CollectionDialogViewModel vm && await vm.TryAcceptAsync())
        {
            Close(true);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
