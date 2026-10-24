using LibOutlook.Ltp;

namespace LibOutlook;

/// <summary>Which line of the message a recipient is on (PidTagRecipientType).</summary>
public enum PstRecipientType
{
    /// <summary>The originator: a sender listed among the recipients.</summary>
    Originator = 0,

    /// <summary>To.</summary>
    To = 1,

    /// <summary>Cc.</summary>
    Cc = 2,

    /// <summary>Bcc.</summary>
    Bcc = 3,
}

/// <summary>A row of a message's recipient table ([MS-PST] 2.4.5.3).</summary>
public sealed class PstRecipient
{
    internal PstRecipient(PstFile file, TableRow row)
    {
        Properties = new PstProperties(file, row);
    }

    /// <summary>All of the recipient's properties.</summary>
    public PstProperties Properties { get; }

    /// <summary>To, Cc or Bcc.</summary>
    public PstRecipientType Type => (PstRecipientType)(Properties.GetInt32(PstPropertyIds.RecipientType) ?? (int)PstRecipientType.To);

    /// <summary>The recipient's name as shown.</summary>
    public string? DisplayName => Properties.GetString(PstPropertyIds.DisplayName) ?? Properties.GetString(PstPropertyIds.RecipientDisplayName);

    /// <summary>The address: SMTP, or an Exchange legacy DN when <see cref="AddressType"/> is "EX".</summary>
    public string? EmailAddress => Properties.GetString(PstPropertyIds.EmailAddress);

    /// <summary>"SMTP" or "EX".</summary>
    public string? AddressType => Properties.GetString(PstPropertyIds.AddressType);

    /// <summary>The SMTP address when the file has one: PidTagSmtpAddress, or the address itself when it is SMTP.</summary>
    public string? SmtpAddress
        => Properties.GetString(PstPropertyIds.SmtpAddress) ?? (string.Equals(AddressType, "SMTP", StringComparison.OrdinalIgnoreCase) ? EmailAddress : null);

    /// <inheritdoc/>
    public override string ToString() => DisplayName ?? SmtpAddress ?? EmailAddress ?? "";
}
