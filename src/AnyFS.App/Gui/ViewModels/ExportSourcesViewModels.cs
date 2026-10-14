using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnyFS.App.Gui.ViewModels;

public sealed partial class ExportSourceItemViewModel(Guid id, string name, string typeName) : ObservableObject
{
    public Guid Id { get; } = id;

    public string Name { get; } = name;

    public string TypeName { get; } = typeName;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = true;
}

/// <summary>Export... (spec 9.5): tick the sources to write to a sources file; all are ticked to begin with.</summary>
public sealed partial class ExportSourcesDialogViewModel : DialogViewModelBase
{
    public ExportSourcesDialogViewModel(IEnumerable<(Guid Id, string Name, string TypeName)> sources)
    {
        Items = sources.Select(s => new ExportSourceItemViewModel(s.Id, s.Name, s.TypeName)).ToList();
    }

    public IReadOnlyList<ExportSourceItemViewModel> Items { get; }

    public IReadOnlyList<Guid> ChosenIds => Items.Where(i => i.IsChecked).Select(i => i.Id).ToList();

    public bool Validate()
    {
        Error = null;
        if (!Items.Any(i => i.IsChecked))
        {
            Error = "Tick at least one source.";
            return false;
        }

        return true;
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items)
        {
            item.IsChecked = true;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in Items)
        {
            item.IsChecked = false;
        }
    }
}
