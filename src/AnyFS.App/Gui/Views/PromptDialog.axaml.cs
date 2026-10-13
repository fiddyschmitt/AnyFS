using AnyFS.App.Gui.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AnyFS.App.Gui.Views;

public partial class PromptDialog : Window
{
    private readonly PromptDialogViewModel _viewModel;

    public PromptDialog(PromptDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Opened += (_, _) =>
        {
            if (viewModel.IsPassword)
            {
                PasswordBox.Focus();
            }
            else if (viewModel.IsText)
            {
                TextBox.Focus();
            }
        };
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(_viewModel.Result(accepted: true));

    private void Yes_Click(object? sender, RoutedEventArgs e) => Close(_viewModel.Result(accepted: true, yes: true));

    private void No_Click(object? sender, RoutedEventArgs e) => Close(_viewModel.Result(accepted: true, yes: false));

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(_viewModel.Result(accepted: false));
}
