namespace AnyFS.Sdk;

public enum SettingKind
{
    /// <summary>Pick from the property type: string is Text, int/long/double Number, bool Bool, enum Choice.</summary>
    Auto,
    Text,
    Multiline,
    Password,
    Number,
    Bool,
    Choice,
    FilePath,
    FolderPath,
}

/// <summary>
/// Describes one property of a settings POCO. The host generates the settings form, the JSON layer and
/// (later) the command line from these; there is no UI code in a plugin.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute(string label) : Attribute
{
    public string Label { get; } = label;

    public string? Hint { get; set; }

    public string? Placeholder { get; set; }

    public bool Required { get; set; }

    /// <summary>Never written to the settings files; stored in the secret store keyed by the owner's id.</summary>
    public bool Secret { get; set; }

    public SettingKind Kind { get; set; } = SettingKind.Auto;

    public string? Group { get; set; }

    public bool Advanced { get; set; }

    public int Order { get; set; }

    /// <summary>For <see cref="SettingKind.Choice"/> on string properties.</summary>
    public string[]? Choices { get; set; }

    /// <summary>
    /// Shown only on these operating systems, named as <see cref="OperatingSystem.IsOSPlatform(string)"/> takes
    /// them: "windows", "linux", "macos". The JSON keeps the value everywhere, so a settings file travels between
    /// machines; the form just leaves the field out elsewhere (and a plugin's Validate should ignore it there).
    /// Null (the default): shown everywhere.
    /// </summary>
    public string[]? Platforms { get; set; }
}

/// <summary>
/// A plugin whose hints or placeholders depend on the machine (an example path for this OS). The host asks
/// for each property after reading its <see cref="SettingAttribute"/>; return null to keep the attribute's text.
/// </summary>
public interface ISettingTextProvider
{
    string? HintFor(string propertyName);

    string? PlaceholderFor(string propertyName);
}

/// <summary>
/// A plugin whose form shows only the fields that apply to what has been entered so far: one source that serves
/// several kinds of upstream, told apart by an address, each with its own way of signing in. The host asks for
/// every property each time a field changes; false hides the field. A hidden field keeps its value in the JSON,
/// so a plugin's Validate should ignore the fields it hides.
/// </summary>
public interface ISettingVisibility
{
    /// <param name="settings">The settings POCO with the form's values already written into it.</param>
    /// <param name="propertyName">The property whose field is in question.</param>
    bool IsVisible(object settings, string propertyName);
}

public sealed record ValidationMessage(string Message, string? Property = null, bool IsError = true);

/// <summary>A plugin that checks its settings before they're saved.</summary>
public interface IValidatable
{
    IEnumerable<ValidationMessage> Validate(object settings);
}

public sealed record TestResult(bool Success, string Message);

/// <summary>A plugin that can try its settings out (the dialog's Test button).</summary>
public interface ITestable
{
    Task<TestResult> TestAsync(object settings, IProgress<string> progress, CancellationToken ct);
}
