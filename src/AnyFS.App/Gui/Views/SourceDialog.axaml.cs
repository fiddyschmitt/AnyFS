using AnyFS.App.Gui.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AnyFS.App.Gui.Views;

public partial class SourceDialog : Window
{
    public SourceDialog(SourceDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // the setting asked first, when the plugin marks one (the name is made from it), else the name
        Opened += (_, _) => (PrimaryField.IsVisible && PrimaryField.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } box ? box : NameBox).Focus();
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
