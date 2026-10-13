using AnyFS.App.Gui.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AnyFS.App.Gui.Views;

public partial class SourceDialog : Window
{
    public SourceDialog(SourceDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Opened += (_, _) => NameBox.Focus();
    }

    private async void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SourceDialogViewModel vm && await vm.TryAcceptAsync())
        {
            Close(true);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
