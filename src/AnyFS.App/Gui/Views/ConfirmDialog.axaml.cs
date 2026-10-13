using AnyFS.App.Gui.Services;
using AnyFS.App.Gui.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AnyFS.App.Gui.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(ConfirmDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Yes_Click(object? sender, RoutedEventArgs e) => Close(ConfirmButton.Yes);

    private void No_Click(object? sender, RoutedEventArgs e) => Close(ConfirmButton.No);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(ConfirmButton.Cancel);
}
