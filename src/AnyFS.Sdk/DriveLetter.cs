namespace AnyFS.Sdk;

/// <summary>
/// A Windows drive letter in a setting: "x", " X: " and @"X:\" all mean X. A to C are never offered (floppies and
/// the system drive). The providers that can map a letter (WinFsp, SMB) share this so the rules agree.
/// </summary>
public static class DriveLetter
{
    /// <summary>The letter as one upper-case character, or null when the text isn't a letter D to Z.</summary>
    public static string? Normalise(string? text)
    {
        var letter = text?.Trim().TrimEnd(':', '\\', '/').ToUpperInvariant();
        return letter is { Length: 1 } && letter[0] is >= 'D' and <= 'Z' ? letter : null;
    }

    /// <summary>The root path of a normalised letter: @"X:\".</summary>
    public static string Root(string letter) => letter + @":\";

    /// <summary>The validation message for text that isn't a letter; null when it is empty or valid.</summary>
    public static ValidationMessage? Validate(string? text, string property)
        => string.IsNullOrWhiteSpace(text) || Normalise(text) is not null
            ? null
            : new ValidationMessage("A drive letter is a single letter, D to Z.", property);
}
