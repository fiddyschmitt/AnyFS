using AnyFS.App.Gui.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AnyFS.App.Gui.Views;

public partial class ChooseDialog : Window
{
    /// <summary>How much of the screen the dialog may take as it fits its choices.</summary>
    private const double MostOfTheScreen = 0.9;

    private readonly ChooseDialogViewModel _viewModel;

    public ChooseDialog(ChooseDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Opened += (_, _) => ChoiceList.Focus();
    }

    /// <summary>Shows the dialog over <paramref name="owner"/>, no bigger than most of its screen; the index chosen, or null.</summary>
    public Task<int?> ShowAsync(Window owner)
    {
        if (owner.Screens?.ScreenFromWindow(owner) is { } screen)
        {
            MaxWidth = screen.WorkingArea.Width / screen.Scaling * MostOfTheScreen;
            MaxHeight = screen.WorkingArea.Height / screen.Scaling * MostOfTheScreen;
        }

        return ShowDialog<int?>(owner);
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(_viewModel.Chosen);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void ChoiceList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // a double-click on a choice is OK; on the scroll bar or below the last, nothing
        if (e.Source is Control control && control.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null && _viewModel.HasChoice)
        {
            Close(_viewModel.Chosen);
        }
    }
}
