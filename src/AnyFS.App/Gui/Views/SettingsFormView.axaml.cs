using AnyFS.App.Gui.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;

namespace AnyFS.App.Gui.Views;

public partial class SettingsFormView : UserControl
{
    public SettingsFormView()
    {
        InitializeComponent();
    }
}

/// <summary>Picks the field template by <see cref="SettingFieldViewModel.TemplateKey"/>.</summary>
public sealed class FieldTemplateSelector : IDataTemplate
{
    [Content]
    public Dictionary<string, IDataTemplate> Templates { get; } = new(StringComparer.Ordinal);

    public Control? Build(object? param)
        => param is SettingFieldViewModel field && Templates.TryGetValue(field.TemplateKey, out var template) ? template.Build(param) : null;

    public bool Match(object? data) => data is SettingFieldViewModel;
}
