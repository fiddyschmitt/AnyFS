namespace AnyFS.Sdk;

public enum PromptKind
{
    Text,
    /// <summary>Masked input: passwords, PINs, one-time codes.</summary>
    Password,
    YesNo,
    Choice,
}

public sealed record PromptRequest(
    PromptKind Kind,
    string Title,
    string Message,
    IReadOnlyList<string>? Choices = null,
    string? DefaultValue = null);

public sealed record PromptResult(bool Answered, string? Text = null, bool? Yes = null, int? ChoiceIndex = null)
{
    public static readonly PromptResult Cancelled = new(false);

    public static PromptResult FromText(string text) => new(true, Text: text);

    public static PromptResult FromYesNo(bool yes) => new(true, Yes: yes);

    public static PromptResult FromChoice(int index) => new(true, ChoiceIndex: index);
}

/// <summary>Per-owner secret storage (keys are scoped by the host to the source or mount).</summary>
public interface ISecretStore
{
    string? Get(string key);

    void Set(string key, string? value);
}
