using AnyFS.Sdk;
using AnyFS.Sources.Outlook.Ews;
using AnyFS.Sources.Outlook.Files;
using AnyFS.Sources.Outlook.Mail;

namespace AnyFS.Sources.Outlook;

/// <summary>A file in an item's folder: made by the source (bytes in hand) or an attachment (read from the store when opened).</summary>
internal sealed class ItemFile(string name, long length, byte[]? bytes, MailAttachment? attachment, string what)
{
    public string Name => name;

    public long Length => length;

    /// <summary>The content, for a file the source makes.</summary>
    public byte[]? Bytes => bytes;

    /// <summary>The attachment, for one of the item's.</summary>
    public MailAttachment? Attachment => attachment;

    /// <summary>What the file is, for the refusal to write ("a message as Markdown").</summary>
    public string What => what;
}

/// <summary>
/// What an item's folder holds (spec 7.7): for a message message.md and message.eml, for an event event.md and event.ics,
/// for a meeting request or answer in a mail folder the message's two files and event.ics, for a task or note one .md;
/// and, when the item has attachments, an "attachments" folder with every one of them (inline images too), an attached
/// message as a nested .eml. The names are fixed and short so that a path stays inside Windows' limit. Made once per
/// open item and kept a while. The .eml is the message as its server holds it where the store has that; else it is
/// made from the properties. An attachment whose size the store only estimates (a mailbox's server's) is read whole
/// then, so that its file's length, and the size the Markdown gives, are its content's.
/// </summary>
internal sealed class ItemFiles
{
    private ItemFiles(MailItem item, MailItemSummary listed, IReadOnlyList<ItemFile> files, IReadOnlyList<ItemFile> attachments)
    {
        Item = item;
        Listed = listed;
        Files = files;
        Attachments = attachments;
    }

    public MailItem Item { get; }

    /// <summary>The item as its folder lists it: the date it is filed under, whether it is an event.</summary>
    public MailItemSummary Listed { get; }

    /// <summary>The files in the item's folder itself.</summary>
    public IReadOnlyList<ItemFile> Files { get; }

    /// <summary>The files in its attachments folder; none means no such folder.</summary>
    public IReadOnlyList<ItemFile> Attachments { get; }

    public bool HasAttachmentsFolder => Attachments.Count > 0;

    public DateTimeOffset? Modified => Listed.Date ?? Item.Modified;

    public DateTimeOffset? Created => Item.Created ?? Listed.Date;

    /// <summary>How many files an item makes in its folder, attachments aside: what a listing counts before any item is opened.</summary>
    public static int CountOf(MailItemSummary item) => item.IsEvent ? 2 : item.Kind switch
    {
        MailItemKind.Message => 2,
        MailItemKind.Appointment => 3,
        _ => 1,
    };

    public ItemFile? File(string name) => Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public ItemFile? Attachment(string name) => Attachments.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public VfsEntry EntryOf(ItemFile file) => VfsEntry.File(file.Name, file.Length, Modified, Created);

    public VfsEntry AttachmentsEntry => VfsEntry.Directory(MailNames.AttachmentsFolder, Modified, Created);

    /// <param name="listed">The item as its folder lists it (an event or not, and the date it is filed under).</param>
    /// <param name="folderName">The item's folder name: a contact's file name.</param>
    /// <param name="keys">What encrypted S/MIME mail is opened with; null opens none.</param>
    public static async Task<ItemFiles> BuildAsync(MailItem item, MailItemSummary listed, string folderName, CancellationToken ct, SmimeKeys? keys = null)
    {
        // a message's .eml is the message as it came; S/MIME mail is opened from it for the Markdown and the attachments
        // folder (signed: the content and its signature checked; encrypted: decrypted with the user's certificate)
        byte[]? eml = null;
        var shown = item;
        if (!listed.IsEvent && item.Kind is MailItemKind.Message or MailItemKind.Appointment)
        {
            eml = item.RawMime ?? MimeWriter.Build(item);
            if (SmimeReader.IsSmime(item))
            {
                shown = SmimeReader.Open(item, eml, keys ?? new SmimeKeys());
            }
        }

        // the attachments' names, fixed first so the Markdown's links and the attachments folder agree; a contact's (its
        // photo) are the vCard's business alone
        var named = AttachmentNames.For(shown.Kind == MailItemKind.Contact ? shown.Attachments : await SettleAsync(shown.Attachments, ct).ConfigureAwait(false));
        var files = new List<ItemFile>();
        if (listed.IsEvent)
        {
            files.Add(Made(MailNames.EventMarkdown, MessageMarkdown.Build(item, named), "an appointment as Markdown"));
            files.Add(Made(MailNames.EventCalendar, CalendarFile.Build(item), "an appointment as iCalendar"));
        }
        else
        {
            switch (item.Kind)
            {
                case MailItemKind.Contact:
                    files.Add(Made(folderName, await ContactFile.BuildAsync(item, ct).ConfigureAwait(false), "a contact as a vCard"));
                    break;
                case MailItemKind.Message:
                case MailItemKind.Appointment:
                    files.Add(Made(MailNames.MessageMarkdown, MessageMarkdown.Build(shown, named), "a message as Markdown"));
                    files.Add(Made(MailNames.MessageMime, eml!, "a message as .eml"));
                    if (item.Kind == MailItemKind.Appointment)
                    {
                        // a meeting request, answer or cancellation: the meeting it is about, to import
                        files.Add(Made(MailNames.EventCalendar, CalendarFile.Build(item), "a meeting as iCalendar"));
                    }

                    break;
                default:
                    files.Add(Made(MailNames.ItemMarkdown(item.Kind), MessageMarkdown.Build(item, named), "an item as Markdown"));
                    break;
            }
        }

        var attachments = new List<ItemFile>(named.Count);
        foreach (var (attachment, name) in named)
        {
            if (attachment.IsMessage)
            {
                var embedded = await attachment.Embedded!(ct).ConfigureAwait(false);
                if (embedded is not null)
                {
                    attachments.Add(Made(name, embedded.RawMime ?? MimeWriter.Build(embedded), "an attached message as .eml"));
                }
            }
            else
            {
                attachments.Add(new ItemFile(name, attachment.Size, null, attachment, "an attachment"));
            }
        }

        return new ItemFiles(item, listed, files, attachments);
    }

    private static ItemFile Made(string name, byte[] bytes, string what) => new(name, bytes.Length, bytes, null, what);

    /// <summary>
    /// The attachments with their sizes right: one the store gives an estimate for is read now and served from memory;
    /// one that is gone by then is left out. Any other trouble fails the item, to be tried again when it is next opened.
    /// </summary>
    private static async Task<IReadOnlyList<MailAttachment>> SettleAsync(IReadOnlyList<MailAttachment> attachments, CancellationToken ct)
    {
        if (attachments.All(a => a.ExactSize || a.IsMessage))
        {
            return attachments;
        }

        var settled = new List<MailAttachment>(attachments.Count);
        foreach (var attachment in attachments)
        {
            if (attachment.ExactSize || attachment.IsMessage)
            {
                settled.Add(attachment);
                continue;
            }

            byte[] content;
            try
            {
                await using var stream = await attachment.OpenAsync(ct).ConfigureAwait(false);
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy, ct).ConfigureAwait(false);
                content = copy.ToArray();
            }
            catch (EwsException ex) when (ex.IsNotFound)
            {
                continue;
            }

            settled.Add(attachment with { Size = content.Length, OpenAsync = _ => Task.FromResult<Stream>(new MemoryStream(content, writable: false)), ExactSize = true });
        }

        return settled;
    }
}

/// <summary>The names a message's attachments show under: safe for a path, told apart from each other, a nameless one numbered.</summary>
internal static class AttachmentNames
{
    public static List<(MailAttachment Attachment, string FileName)> For(IReadOnlyList<MailAttachment> attachments)
    {
        var named = new List<(MailAttachment, string)>(attachments.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unnamed = 0;
        foreach (var attachment in attachments)
        {
            var name = MailNames.FileName(string.IsNullOrWhiteSpace(attachment.Name) ? $"attachment {++unnamed}" : attachment.Name);
            if (!used.Add(name))
            {
                for (var n = 2; ; n++)
                {
                    var candidate = MailNames.NumberedFile(name, n);
                    if (used.Add(candidate))
                    {
                        name = candidate;
                        break;
                    }
                }
            }

            named.Add((attachment, name));
        }

        return named;
    }

    /// <summary>A lookup of the names, by attachment.</summary>
    public static Func<MailAttachment, string> LookupFor(IReadOnlyList<MailAttachment> attachments)
    {
        var named = For(attachments);
        return a => named.First(n => ReferenceEquals(n.Attachment, a)).FileName;
    }
}
