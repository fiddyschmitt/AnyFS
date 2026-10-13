using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AnyFS.App.Gui.Services;
using AnyFS.Core.Settings;
using AnyFS.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnyFS.App.Gui.ViewModels;

/// <summary>
/// The settings form a plugin gets for free (spec 6.7): one field per <c>[Setting]</c> property, grouped,
/// Advanced ones collapsed, validation inline, a Test button when the plugin is <see cref="ITestable"/>.
/// Reads and writes the plugin's POCO by reflection; the annotation on <c>SettingsType</c> keeps the
/// properties in trimmed builds.
/// </summary>
public sealed partial class SettingsFormViewModel : ObservableObject
{
    private readonly IValidatable? _validator;
    private readonly ITestable? _tester;

    public SettingsFormViewModel(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)] Type settingsType,
        object settings,
        IValidatable? validator,
        ITestable? tester,
        ISettingTextProvider? texts,
        IDialogs dialogs)
    {
        Settings = settings;
        _validator = validator;
        _tester = tester;
        Fields = PluginSettingsBinder.Describe(settingsType).Select(p => new SettingFieldViewModel(p, texts, dialogs)).ToList();
        foreach (var field in Fields)
        {
            field.Load(settings);
        }

        var groups = new List<SettingGroupViewModel>();
        foreach (var group in Fields.Where(f => !f.IsAdvanced).GroupBy(f => f.Group ?? ""))
        {
            groups.Add(new SettingGroupViewModel(group.Key, isAdvanced: false, group.ToList()));
        }

        var advanced = Fields.Where(f => f.IsAdvanced).ToList();
        if (advanced.Count > 0)
        {
            groups.Add(new SettingGroupViewModel("Advanced", isAdvanced: true, advanced));
        }

        Groups = new ObservableCollection<SettingGroupViewModel>(groups);
        TestCommand = new AsyncRelayCommand(TestAsync, () => HasTest && !TestRunning);
    }

    public object Settings { get; }

    public IReadOnlyList<SettingFieldViewModel> Fields { get; }

    public ObservableCollection<SettingGroupViewModel> Groups { get; }

    public bool HasTest => _tester is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    public partial bool TestRunning { get; set; }

    [ObservableProperty]
    public partial string? TestMessage { get; set; }

    [ObservableProperty]
    public partial bool TestSucceeded { get; set; }

    /// <summary>A validation message that names no property.</summary>
    [ObservableProperty]
    public partial string? FormError { get; set; }

    public AsyncRelayCommand TestCommand { get; }

    /// <summary>Writes the fields into the POCO and checks them (required, numbers, then the plugin's own rules). False when anything is wrong.</summary>
    public bool Validate()
    {
        var ok = true;
        FormError = null;
        foreach (var field in Fields)
        {
            field.Error = field.Apply(Settings);
            field.Warning = null;
            ok &= field.Error is null;
        }

        if (!ok || _validator is null)
        {
            return ok;
        }

        foreach (var message in _validator.Validate(Settings))
        {
            var field = message.Property is null ? null : Fields.FirstOrDefault(f => f.PropertyName == message.Property);
            if (!message.IsError)
            {
                if (field is not null)
                {
                    field.Warning = field.Warning is null ? message.Message : field.Warning + "\n" + message.Message;
                }

                continue;
            }

            ok = false;
            if (field is not null)
            {
                field.Error = field.Error is null ? message.Message : field.Error + "\n" + message.Message;
            }
            else
            {
                FormError = FormError is null ? message.Message : FormError + "\n" + message.Message;
            }
        }

        return ok;
    }

    private async Task TestAsync()
    {
        if (_tester is null || !Validate())
        {
            return;
        }

        TestRunning = true;
        TestSucceeded = false;
        TestMessage = "Testing...";
        try
        {
            var progress = new Progress<string>(message => TestMessage = message);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var result = await _tester.TestAsync(Settings, progress, cts.Token);
            TestSucceeded = result.Success;
            TestMessage = result.Message;
        }
        catch (OperationCanceledException)
        {
            TestMessage = "The test timed out.";
        }
        catch (Exception ex)
        {
            TestMessage = ex.GetBaseException().Message;
        }
        finally
        {
            TestRunning = false;
        }
    }
}

public sealed class SettingGroupViewModel(string name, bool isAdvanced, IReadOnlyList<SettingFieldViewModel> fields)
{
    public string Name { get; } = name;

    public bool HasName => Name.Length > 0 && !IsAdvanced;

    public bool IsAdvanced { get; } = isAdvanced;

    public IReadOnlyList<SettingFieldViewModel> Fields { get; } = fields;
}

/// <summary>One property of the settings POCO as a form field.</summary>
public sealed partial class SettingFieldViewModel : ObservableObject
{
    private readonly SettingProperty _property;
    private readonly IDialogs _dialogs;
    private readonly Type _type;

    public SettingFieldViewModel(SettingProperty property, ISettingTextProvider? texts, IDialogs dialogs)
    {
        _property = property;
        _dialogs = dialogs;
        _type = Nullable.GetUnderlyingType(property.Property.PropertyType) ?? property.Property.PropertyType;
        Kind = ResolveKind(property.Attribute, _type);
        Choices = Kind == SettingKind.Choice
            ? property.Attribute.Choices ?? (_type.IsEnum ? Enum.GetNames(_type) : [])
            : [];
        Hint = texts?.HintFor(property.Name) ?? property.Attribute.Hint;
        Placeholder = texts?.PlaceholderFor(property.Name) ?? property.Attribute.Placeholder ?? "";
        BrowseCommand = new AsyncRelayCommand(BrowseAsync);
    }

    public string PropertyName => _property.Name;

    public string Label => _property.Attribute.Label;

    public string LabelWithMark => _property.Attribute.Required ? Label + " *" : Label;

    /// <summary>The attribute's hint, unless the plugin (an <see cref="ISettingTextProvider"/>) gave one for this machine.</summary>
    public string? Hint { get; }

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    public string Placeholder { get; }

    public bool IsRequired => _property.Attribute.Required;

    public bool IsSecret => _property.Attribute.Secret;

    public string? Group => _property.Attribute.Group;

    public bool IsAdvanced => _property.Attribute.Advanced;

    public SettingKind Kind { get; }

    /// <summary>Which template the form uses: Text, Multiline, Password, Number, Bool, Choice or Path.</summary>
    public string TemplateKey => Kind switch
    {
        SettingKind.Multiline => "Multiline",
        SettingKind.Password => "Password",
        SettingKind.Number => "Number",
        SettingKind.Bool => "Bool",
        SettingKind.Choice => "Choice",
        SettingKind.FilePath or SettingKind.FolderPath => "Path",
        _ => "Text",
    };

    public IReadOnlyList<string> Choices { get; }

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial bool Checked { get; set; }

    [ObservableProperty]
    public partial string? SelectedChoice { get; set; }

    [ObservableProperty]
    public partial bool Reveal { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial string? Warning { get; set; }

    public bool HasWarning => Warning is not null;

    public AsyncRelayCommand BrowseCommand { get; }

    public void Load(object settings)
    {
        var value = _property.Property.GetValue(settings);
        switch (Kind)
        {
            case SettingKind.Bool:
                Checked = value is true;
                break;
            case SettingKind.Choice:
                SelectedChoice = value?.ToString() is { } s && Choices.Contains(s) ? s : Choices.FirstOrDefault();
                break;
            default:
                Text = value switch
                {
                    null => "",
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _ => value.ToString() ?? "",
                };
                break;
        }
    }

    /// <summary>Writes the field into the POCO; returns the error to show, or null.</summary>
    public string? Apply(object settings)
    {
        var nullable = !_type.IsValueType || Nullable.GetUnderlyingType(_property.Property.PropertyType) is not null;
        try
        {
            switch (Kind)
            {
                case SettingKind.Bool:
                    _property.Property.SetValue(settings, Checked);
                    return null;

                case SettingKind.Choice:
                    if (SelectedChoice is null)
                    {
                        return IsRequired ? "Choose one." : null;
                    }

                    _property.Property.SetValue(settings, _type.IsEnum ? Enum.Parse(_type, SelectedChoice) : SelectedChoice);
                    return null;

                case SettingKind.Number:
                    var text = Text.Trim();
                    if (text.Length == 0)
                    {
                        if (IsRequired)
                        {
                            return "Required.";
                        }

                        if (nullable)
                        {
                            _property.Property.SetValue(settings, null);
                            return null;
                        }

                        return "Enter a number.";
                    }

                    if (!TryParseNumber(text, out var number))
                    {
                        return "Enter a whole number." + (_type == typeof(int) || _type == typeof(long) ? "" : " Decimals are allowed.");
                    }

                    _property.Property.SetValue(settings, number);
                    return null;

                default:
                    var value = Kind == SettingKind.Multiline ? Text.Replace("\r\n", "\n") : Text.Trim();
                    if (value.Length == 0 && IsRequired)
                    {
                        return "Required.";
                    }

                    _property.Property.SetValue(settings, value.Length == 0 && nullable ? null : value);
                    return null;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or System.Reflection.TargetInvocationException)
        {
            return ex.GetBaseException().Message;
        }
    }

    private bool TryParseNumber(string text, out object? value)
    {
        value = null;
        var style = NumberStyles.Integer;
        var culture = CultureInfo.InvariantCulture;
        if (_type == typeof(int) && int.TryParse(text, style, culture, out var i))
        {
            value = i;
        }
        else if (_type == typeof(long) && long.TryParse(text, style, culture, out var l))
        {
            value = l;
        }
        else if (_type == typeof(double) && double.TryParse(text, NumberStyles.Float, culture, out var d))
        {
            value = d;
        }
        else if (_type == typeof(float) && float.TryParse(text, NumberStyles.Float, culture, out var f))
        {
            value = f;
        }
        else if (_type == typeof(decimal) && decimal.TryParse(text, NumberStyles.Number, culture, out var m))
        {
            value = m;
        }
        else if (_type == typeof(string))
        {
            value = text;
        }

        return value is not null;
    }

    private async Task BrowseAsync()
    {
        var picked = Kind == SettingKind.FolderPath ? await _dialogs.PickFolderAsync(Text) : await _dialogs.PickFileAsync(Text);
        if (picked is not null)
        {
            Text = picked;
        }
    }

    private static SettingKind ResolveKind(SettingAttribute attribute, Type type)
    {
        if (attribute.Kind != SettingKind.Auto)
        {
            return attribute.Kind;
        }

        if (attribute.Secret)
        {
            return SettingKind.Password;
        }

        if (type == typeof(bool))
        {
            return SettingKind.Bool;
        }

        if (type.IsEnum || attribute.Choices is { Length: > 0 })
        {
            return SettingKind.Choice;
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            return SettingKind.Number;
        }

        return SettingKind.Text;
    }
}
