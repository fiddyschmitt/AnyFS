using System.Runtime.InteropServices;
using AnyFS.App.Gui.Services;
using AnyFS.Core.Model;
using AnyFS.Core.Runtime;
using AnyFS.Core.Settings;
using AnyFS.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnyFS.App.Gui.ViewModels;

/// <summary>A dialog with OK and Cancel: OK runs <see cref="OnAccept"/>, which either succeeds (the dialog closes) or sets <see cref="Error"/>.</summary>
public abstract partial class DialogViewModelBase : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    public partial bool Busy { get; set; }

    public Func<Task<bool>>? OnAccept { get; set; }

    public async Task<bool> TryAcceptAsync()
    {
        Error = null;
        Busy = true;
        try
        {
            return OnAccept is null || await OnAccept();
        }
        catch (Exception ex)
        {
            Error = ex is ArgumentException or InvalidOperationException ? ex.Message : ex.GetBaseException().Message;
            return false;
        }
        finally
        {
            Busy = false;
        }
    }
}

public sealed partial class CollectionDialogViewModel(string title, string name) : DialogViewModelBase
{
    public string Title { get; } = title;

    [ObservableProperty]
    public partial string Name { get; set; } = name;
}

public sealed class PluginChoiceViewModel(ISourcePlugin plugin)
{
    public ISourcePlugin Plugin { get; } = plugin;

    public string TypeId => Plugin.TypeId;

    public string DisplayName => Plugin.DisplayName;

    public string Description => Plugin.Description;

    public string Initial => Plugin.DisplayName.Length > 0 ? Plugin.DisplayName[..1].ToUpperInvariant() : "?";
}

/// <summary>The Add source / Edit source dialog (spec 11.1): plugin, name, path and read-only (in a collection), then the plugin's form.</summary>
public sealed partial class SourceDialogViewModel : DialogViewModelBase
{
    public const string NameTip = "Tip: use the server's name, e.g. aus-files-01";

    private readonly IDialogs _dialogs;
    private bool _pathFollowsName;
    private bool _settingPath;

    public SourceDialogViewModel(AnyFsEngine engine, IDialogs dialogs, SourceDefinition? existing, SourceMembership? membership, bool askPath, bool readOnlyLocked = false)
    {
        _dialogs = dialogs;
        IsNew = existing is null;
        AskPath = askPath;
        Title = existing is null ? "Add source" : $"Edit source: {existing.Name}";
        PluginChoices = engine.Registry.SourcePlugins.Values.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).Select(p => new PluginChoiceViewModel(p)).ToList();
        ReadOnlyLocked = readOnlyLocked;
        CountFilesOnStart = engine.Settings.App.CountFilesOnStart;
        PathCaption = "Where the source appears in this collection. Shorten to just 'files' to get " + ExampleAccessPath("files", "c") + ".";

        if (existing is null)
        {
            _pathFollowsName = true;
            SelectedPlugin = PluginChoices.FirstOrDefault();
        }
        else
        {
            Name = existing.Name;
            Enabled = existing.Enabled;
            CountFilesOnStart = existing.CountFilesOnStart;
            SelectedPlugin = PluginChoices.FirstOrDefault(p => p.TypeId.Equals(existing.Type, StringComparison.OrdinalIgnoreCase));
            if (SelectedPlugin is not null && engine.SourceSettingsFor(existing) is { } settings)
            {
                Form = BuildForm(SelectedPlugin.Plugin, settings);
            }
            else if (SelectedPlugin is null)
            {
                Error = $"The source type '{existing.Type}' is not available in this build, so its settings can't be edited.";
            }

            if (membership is not null)
            {
                SetPath(membership.Path);
                ReadOnly = membership.ReadOnly || readOnlyLocked;
            }
        }
    }

    public string Title { get; }

    public bool IsNew { get; }

    /// <summary>False when adding from the All sources tab: the source belongs to no collection yet.</summary>
    public bool AskPath { get; }

    public string PathCaption { get; }

    public IReadOnlyList<PluginChoiceViewModel> PluginChoices { get; }

    [ObservableProperty]
    public partial PluginChoiceViewModel? SelectedPlugin { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Path { get; set; } = "";

    [ObservableProperty]
    public partial bool ReadOnly { get; set; }

    public bool ReadOnlyLocked { get; }

    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    [ObservableProperty]
    public partial bool CountFilesOnStart { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForm))]
    public partial SettingsFormViewModel? Form { get; set; }

    public bool HasForm => Form is not null;

    public string DefaultPath => SelectedPlugin is null ? "" : Name.Trim().Length == 0 ? SelectedPlugin.Plugin.TypeFolder : SettingsStore.DefaultSourcePath(SelectedPlugin.Plugin, Name.Trim());

    public object? Settings => Form?.Settings;

    /// <summary>Name, path and the form; sets the errors. False when anything is wrong.</summary>
    public bool Validate()
    {
        Error = null;
        if (SelectedPlugin is null)
        {
            Error = "Choose a source type.";
            return false;
        }

        if (Name.Trim().Length == 0)
        {
            Error = "Give the source a name. " + NameTip;
            return false;
        }

        if (AskPath && !VfsPath.TryNormalizeSourcePath(Path, out _, out var reason))
        {
            Error = $"The path isn't valid: {reason}";
            return false;
        }

        if (Form is null || !Form.Validate())
        {
            Error = Form?.FormError ?? "Check the settings.";
            return false;
        }

        return true;
    }

    partial void OnSelectedPluginChanged(PluginChoiceViewModel? value)
    {
        if (IsNew && value is not null)
        {
            Form = BuildForm(value.Plugin, value.Plugin.CreateDefaultSettings());
        }

        OnPropertyChanged(nameof(DefaultPath));
        if (_pathFollowsName)
        {
            SetPath(DefaultPath);
        }
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(DefaultPath));
        if (_pathFollowsName)
        {
            SetPath(DefaultPath);
        }
    }

    partial void OnPathChanged(string value)
    {
        if (!_settingPath && value != DefaultPath)
        {
            // edited by hand: stop following the name (spec 4.1)
            _pathFollowsName = false;
        }
    }

    private void SetPath(string path)
    {
        _settingPath = true;
        try
        {
            Path = path;
        }
        finally
        {
            _settingPath = false;
        }
    }

    private SettingsFormViewModel BuildForm(ISourcePlugin plugin, object settings)
        => new(plugin.SettingsType, settings, plugin as IValidatable, plugin as ITestable, _dialogs);

    private static string ExampleAccessPath(string sourcePath, string tail)
        => OperatingSystem.IsWindows() ? $@"\\anyfs\ben\{sourcePath}\{tail}" : $"~/anyfs/ben/{sourcePath}/{tail}";
}

/// <summary>"Existing source ▸": only the path and read-only are asked (spec 11.1).</summary>
public sealed partial class ExistingSourceDialogViewModel : DialogViewModelBase
{
    public ExistingSourceDialogViewModel(string sourceName, string defaultPath, string collectionName)
    {
        SourceName = sourceName;
        Path = defaultPath;
        Title = $"Add {sourceName} to {collectionName}";
    }

    public string Title { get; }

    public string SourceName { get; }

    [ObservableProperty]
    public partial string Path { get; set; }

    [ObservableProperty]
    public partial bool ReadOnly { get; set; }

    public bool Validate()
    {
        Error = null;
        if (!VfsPath.TryNormalizeSourcePath(Path, out _, out var reason))
        {
            Error = $"The path isn't valid: {reason}";
            return false;
        }

        return true;
    }
}

public sealed class ProviderChoiceViewModel(IMountProviderPlugin provider)
{
    public IMountProviderPlugin Provider { get; } = provider;

    public string TypeId => Provider.TypeId;

    public string DisplayName => Provider.DisplayName;

    public string Description => Provider.Description;

    public PerformanceTier Tier => Provider.Tier;

    public bool OnThisOs { get; } = provider.SupportedPlatforms.Any(RuntimeInformation.IsOSPlatform);

    public string? UnavailableReason => !OnThisOs ? "Not available on this operating system" : Provider.UnavailableReason;

    public bool IsAvailable => UnavailableReason is null;

    public string Caption => IsAvailable ? Description : $"{Description}  ({UnavailableReason})";

    public string Initial => Provider.DisplayName.Length > 0 ? Provider.DisplayName[..1].ToUpperInvariant() : "?";
}

/// <summary>The Add mount / Edit mount dialog (spec 11.1): provider (unavailable ones greyed with the reason), name, read-only, the provider's form.</summary>
public sealed partial class MountDialogViewModel : DialogViewModelBase
{
    private readonly IDialogs _dialogs;
    private readonly ICollectionInfo _collection;
    private bool _nameFollowsProvider;
    private bool _settingName;

    public MountDialogViewModel(AnyFsEngine engine, IDialogs dialogs, ICollectionInfo collection, MountDefinition? existing)
    {
        _dialogs = dialogs;
        _collection = collection;
        IsNew = existing is null;
        Title = existing is null ? $"Add mount to {collection.Name}" : $"Edit mount: {existing.Name}";
        Providers = engine.Registry.MountProviders.Values
            .Select(p => new ProviderChoiceViewModel(p))
            .OrderBy(p => p.IsAvailable ? 0 : 1)
            .ThenBy(p => p.Tier)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (existing is null)
        {
            _nameFollowsProvider = true;
            SelectedProvider = Providers.FirstOrDefault(p => p.IsAvailable) ?? Providers.FirstOrDefault();
        }
        else
        {
            SetName(existing.Name);
            Enabled = existing.Enabled;
            SelectedProvider = Providers.FirstOrDefault(p => p.TypeId.Equals(existing.Provider, StringComparison.OrdinalIgnoreCase));
            if (SelectedProvider is not null && engine.MountSettingsFor(existing, collection) is { } settings)
            {
                Form = BuildForm(SelectedProvider.Provider, settings);
            }
            else if (SelectedProvider is null)
            {
                Error = $"The mount provider '{existing.Provider}' is not available in this build, so its settings can't be edited.";
            }

            ReadOnly = existing.ReadOnly || ReadOnlyLocked;
        }
    }

    public string Title { get; }

    public bool IsNew { get; }

    public IReadOnlyList<ProviderChoiceViewModel> Providers { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadOnlyLocked))]
    [NotifyPropertyChangedFor(nameof(ProviderUnavailableReason))]
    [NotifyPropertyChangedFor(nameof(ProviderUnavailable))]
    public partial ProviderChoiceViewModel? SelectedProvider { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    [ObservableProperty]
    public partial bool ReadOnly { get; set; }

    /// <summary>The provider can't write (plain HTTP): read-only is shown ticked and disabled.</summary>
    public bool ReadOnlyLocked => SelectedProvider is { Provider.SupportsWrites: false };

    public string? ProviderUnavailableReason => SelectedProvider?.UnavailableReason;

    public bool ProviderUnavailable => ProviderUnavailableReason is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForm))]
    public partial SettingsFormViewModel? Form { get; set; }

    public bool HasForm => Form is not null;

    public object? Settings => Form?.Settings;

    public bool Validate()
    {
        Error = null;
        if (SelectedProvider is null)
        {
            Error = "Choose a mount provider.";
            return false;
        }

        if (Name.Trim().Length == 0)
        {
            Error = "Give the mount a name.";
            return false;
        }

        if (Form is null || !Form.Validate())
        {
            Error = Form?.FormError ?? "Check the settings.";
            return false;
        }

        return true;
    }

    partial void OnSelectedProviderChanged(ProviderChoiceViewModel? value)
    {
        if (IsNew && value is not null)
        {
            Form = BuildForm(value.Provider, value.Provider.CreateDefaultSettings(_collection));
            if (_nameFollowsProvider)
            {
                SetName(value.DisplayName);
            }
        }

        if (ReadOnlyLocked)
        {
            ReadOnly = true;
        }
    }

    partial void OnNameChanged(string value)
    {
        if (!_settingName && value != SelectedProvider?.DisplayName)
        {
            _nameFollowsProvider = false;
        }
    }

    private void SetName(string name)
    {
        _settingName = true;
        try
        {
            Name = name;
        }
        finally
        {
            _settingName = false;
        }
    }

    private SettingsFormViewModel BuildForm(IMountProviderPlugin provider, object settings)
        => new(provider.SettingsType, settings, provider as IValidatable, provider as ITestable, _dialogs);
}

/// <summary>A plugin's question to the user (spec 6.4, 11.4).</summary>
public sealed partial class PromptDialogViewModel : ObservableObject
{
    public PromptDialogViewModel(string sourceName, PromptRequest request)
    {
        SourceName = sourceName;
        Request = request;
        Text = request.DefaultValue ?? "";
        Choices = request.Choices ?? [];
        SelectedChoice = Choices.FirstOrDefault();
    }

    public string SourceName { get; }

    public PromptRequest Request { get; }

    public string Title => Request.Title;

    public string Message => Request.Message;

    public bool HasMessage => Request.Message.Length > 0;

    public bool IsText => Request.Kind == PromptKind.Text;

    public bool IsPassword => Request.Kind == PromptKind.Password;

    public bool IsYesNo => Request.Kind == PromptKind.YesNo;

    public bool IsChoice => Request.Kind == PromptKind.Choice;

    public bool HasOkCancel => !IsYesNo;

    public IReadOnlyList<string> Choices { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial string? SelectedChoice { get; set; }

    [ObservableProperty]
    public partial bool Reveal { get; set; }

    /// <summary>The answer for OK (or Yes/No), or Cancelled.</summary>
    public PromptResult Result(bool accepted, bool? yes = null)
    {
        if (!accepted)
        {
            return PromptResult.Cancelled;
        }

        return Request.Kind switch
        {
            PromptKind.YesNo => PromptResult.FromYesNo(yes ?? false),
            PromptKind.Choice => SelectedChoice is null ? PromptResult.Cancelled : PromptResult.FromChoice(Choices.ToList().IndexOf(SelectedChoice)),
            _ => PromptResult.FromText(Text),
        };
    }
}

/// <summary>Yes/No/Cancel with a message and an optional checkbox ("Also delete the source definition").</summary>
public sealed partial class ConfirmDialogViewModel : ObservableObject
{
    public ConfirmDialogViewModel(string title, string message, string yesText = "OK", string? noText = null, string? cancelText = "Cancel", string? checkboxText = null, bool checkboxDefault = false)
    {
        Title = title;
        Message = message;
        YesText = yesText;
        NoText = noText;
        CancelText = cancelText;
        CheckboxText = checkboxText;
        Checked = checkboxDefault;
    }

    public string Title { get; }

    public string Message { get; }

    public string YesText { get; }

    public string? NoText { get; }

    public bool HasNo => NoText is not null;

    public string? CancelText { get; }

    public bool HasCancel => CancelText is not null;

    public string? CheckboxText { get; }

    public bool HasCheckbox => CheckboxText is not null;

    [ObservableProperty]
    public partial bool Checked { get; set; }
}

public sealed class TextDialogViewModel(string title, string text, ITextClipboard clipboard)
{
    public string Title { get; } = title;

    public string Text { get; } = text;

    public IAsyncRelayCommand CopyCommand { get; } = new AsyncRelayCommand(() => clipboard.SetTextAsync(text));
}
