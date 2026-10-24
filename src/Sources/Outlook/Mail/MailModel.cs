namespace AnyFS.Sources.Outlook.Mail;

/// <summary>What kind of item a message is, by its message class.</summary>
internal enum MailItemKind
{
    Message,
    Appointment,
    Contact,
    Task,
    Note,
    Other,
}

/// <summary>A folder of the mailbox as the store lists it: its children and how many items it holds.</summary>
/// <param name="Id">The store's id for it (a PST's node id, an EWS folder id), stable while the store is open.</param>
/// <param name="ContainerClass">"IPF.Note", "IPF.Contact", "IPF.Appointment"...; null when the folder says nothing.</param>
internal sealed record MailFolder(string Id, string Name, string? ContainerClass, IReadOnlyList<MailFolder> Folders, int ItemCount, DateTimeOffset? Created, DateTimeOffset? Modified)
{
    public bool IsCalendar => ContainerClass?.StartsWith("IPF.Appointment", StringComparison.OrdinalIgnoreCase) == true;

    public bool IsContacts => ContainerClass?.StartsWith("IPF.Contact", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>What a listing needs of an item without opening it: enough for its folder's name and dates.</summary>
/// <param name="Date">The date the item is filed under: received, else sent, else last changed; an appointment's start.</param>
internal sealed record MailItemSummary(string Id, MailItemKind Kind, string Subject, string? Sender, DateTimeOffset? Date, DateTimeOffset? Created, DateTimeOffset? Modified, long Size, bool HasAttachments);

/// <summary>A name and an address, either of which may be missing.</summary>
internal sealed record MailAddress(string? Name, string? Address)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(Address);

    /// <summary>"Name &lt;address&gt;", or whichever is there.</summary>
    public override string ToString()
    {
        if (string.IsNullOrWhiteSpace(Address))
        {
            return Name ?? "";
        }

        return string.IsNullOrWhiteSpace(Name) || string.Equals(Name, Address, StringComparison.OrdinalIgnoreCase) ? Address : $"{Name} <{Address}>";
    }
}

internal enum MailRecipientKind
{
    To,
    Cc,
    Bcc,
}

/// <summary>A recipient, or an attendee: for a meeting, how they answered.</summary>
internal sealed record MailRecipient(MailRecipientKind Kind, MailAddress Address, string? Response = null);

/// <summary>An attachment: a file, an inline image, or another message.</summary>
/// <param name="Name">The file name as the message gives it (not yet made safe for a path, nor told apart from another of the name).</param>
/// <param name="OpenAsync">The content, as a seekable stream; empty for an embedded message.</param>
/// <param name="Embedded">The attached message, for an attachment that is one; null otherwise.</param>
internal sealed record MailAttachment(string Name, string ContentType, string? ContentId, bool IsInline, long Size, Func<CancellationToken, Task<Stream>> OpenAsync, Func<CancellationToken, Task<MailItem?>>? Embedded, bool IsContactPhoto = false)
{
    public bool IsMessage => Embedded is not null;
}

/// <summary>What an appointment adds to a message.</summary>
/// <param name="RecurrencePattern">Outlook's words for the recurrence ("every Monday"), for a recurring appointment; the occurrences are not expanded by the PST store.</param>
internal sealed record AppointmentDetail(DateTimeOffset? Start, DateTimeOffset? End, bool AllDay, string? Location, bool IsRecurring, string? RecurrencePattern, string? BusyStatus, string? Uid);

/// <summary>A contact's fields.</summary>
internal sealed record ContactDetail(
    string? FileAs,
    string? Prefix,
    string? GivenName,
    string? MiddleName,
    string? Surname,
    string? Suffix,
    string? Nickname,
    string? Company,
    string? JobTitle,
    string? Department,
    IReadOnlyList<string> Emails,
    IReadOnlyList<(string Type, string Number)> Phones,
    IReadOnlyList<(string Type, string Street, string City, string State, string PostalCode, string Country)> Addresses,
    string? WebPage,
    string? ImAddress,
    DateTimeOffset? Birthday,
    DateTimeOffset? Anniversary);

/// <summary>An item opened: everything the files made of it need.</summary>
internal sealed class MailItem
{
    public required MailItemSummary Summary { get; init; }

    public required string MessageClass { get; init; }

    public MailAddress From { get; init; } = new(null, null);

    public IReadOnlyList<MailRecipient> Recipients { get; init; } = [];

    public DateTimeOffset? Sent { get; init; }

    public DateTimeOffset? Received { get; init; }

    public DateTimeOffset? Created { get; init; }

    public DateTimeOffset? Modified { get; init; }

    public string? InternetMessageId { get; init; }

    public string? InReplyTo { get; init; }

    public string? References { get; init; }

    /// <summary>The Internet headers as received, for a message that came in over SMTP.</summary>
    public string? TransportHeaders { get; init; }

    public string? BodyText { get; init; }

    public string? BodyHtml { get; init; }

    public string? BodyRtf { get; init; }

    /// <summary>The message as the server holds it (RFC 5322), when the store has it: what the .eml is, lossless.</summary>
    public byte[]? RawMime { get; init; }

    /// <summary>0 low, 1 normal, 2 high; null when unset.</summary>
    public int? Importance { get; init; }

    public bool IsRead { get; init; }

    public bool IsDraft { get; init; }

    public IReadOnlyList<MailAttachment> Attachments { get; init; } = [];

    public AppointmentDetail? Appointment { get; init; }

    public ContactDetail? Contact { get; init; }

    public MailItemKind Kind => Summary.Kind;

    public string Subject => Summary.Subject;

    public IEnumerable<MailRecipient> To => Recipients.Where(r => r.Kind == MailRecipientKind.To);

    public IEnumerable<MailRecipient> Cc => Recipients.Where(r => r.Kind == MailRecipientKind.Cc);

    public IEnumerable<MailRecipient> Bcc => Recipients.Where(r => r.Kind == MailRecipientKind.Bcc);
}

/// <summary>
/// Where a mailbox's folders and items come from (spec 7.7): a PST or OST file, or a mailbox on its server through
/// Outlook on the web's service, over the same shape. Read concurrently once refreshed.
/// </summary>
internal interface IMailStore : IDisposable
{
    /// <summary>What to call the mailbox: the store's display name, the file's name, the account.</summary>
    string Name { get; }

    /// <summary>The mailbox's top-level folders (Inbox, Sent Items, Calendar...), each with its children; empty before the first <see cref="RefreshAsync"/>.</summary>
    IReadOnlyList<MailFolder> Folders { get; }

    /// <summary>The items a folder holds, as a listing needs them.</summary>
    Task<IReadOnlyList<MailItemSummary>> ItemsAsync(MailFolder folder, CancellationToken ct);

    /// <summary>An item with everything in it; null when the store no longer has it.</summary>
    Task<MailItem?> OpenAsync(string itemId, CancellationToken ct);

    /// <summary>Reads the folders (and what changed in them) afresh: the first call fills the store, later ones take in changes.</summary>
    Task RefreshAsync(CancellationToken ct);
}

/// <summary>The kinds, from a message class ([MS-OXCMSG] 2.2.1.3).</summary>
internal static class MailItemKinds
{
    public static MailItemKind From(string? messageClass)
    {
        var c = messageClass ?? "";
        if (c.StartsWith("IPM.Appointment", StringComparison.OrdinalIgnoreCase) || c.StartsWith("IPM.Schedule.Meeting", StringComparison.OrdinalIgnoreCase) || c.StartsWith("IPM.OLE.CLASS.{00061055-0000-0000-C000-000000000046}", StringComparison.OrdinalIgnoreCase))
        {
            return MailItemKind.Appointment;
        }

        if (c.StartsWith("IPM.Contact", StringComparison.OrdinalIgnoreCase) || c.StartsWith("IPM.DistList", StringComparison.OrdinalIgnoreCase))
        {
            return MailItemKind.Contact;
        }

        if (c.StartsWith("IPM.Task", StringComparison.OrdinalIgnoreCase))
        {
            return MailItemKind.Task;
        }

        if (c.StartsWith("IPM.StickyNote", StringComparison.OrdinalIgnoreCase))
        {
            return MailItemKind.Note;
        }

        if (c.Length == 0 || c.StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase) || c.Equals("IPM", StringComparison.OrdinalIgnoreCase) || c.StartsWith("IPM.Post", StringComparison.OrdinalIgnoreCase) || c.StartsWith("REPORT.", StringComparison.OrdinalIgnoreCase))
        {
            return MailItemKind.Message;
        }

        return MailItemKind.Other;
    }

    /// <summary>Whether Outlook shows a folder of this class in its folder list: its own settings, caches and feeds it does not.</summary>
    public static bool IsShownFolder(string? containerClass, string name)
    {
        var c = containerClass ?? "";
        if (name.StartsWith('{') && name.EndsWith('}'))
        {
            return false;
        }

        if (c.Equals("IPF", StringComparison.OrdinalIgnoreCase) || c.StartsWith("IPF.Configuration", StringComparison.OrdinalIgnoreCase) || c.StartsWith("IPF.Files", StringComparison.OrdinalIgnoreCase)
            || c.StartsWith("IPF.StoreItem", StringComparison.OrdinalIgnoreCase) || c.StartsWith("IPF.ShortcutFolder", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (c.StartsWith("IPF.Contact.", StringComparison.OrdinalIgnoreCase))
        {
            // the people caches Outlook keeps for itself; Companies and the like
            return false;
        }

        return true;
    }
}
