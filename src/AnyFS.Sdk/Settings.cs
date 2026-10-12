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
