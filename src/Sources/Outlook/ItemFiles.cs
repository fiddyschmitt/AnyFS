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
/// What an item's folder holds (spec 7.7): for a message "name.md" and "name.eml", for an appointment event.md and
/// event.ics, for a task or note "name.md"; and, when the item has attachments, an "attachments" folder with every one
/// of them (inline images too), an attached message as a nested .eml. Made once per open item and kept a while. The
/// .eml is the message as its server holds it where the store has that; else it is made from the properties. An
/// attachment whose size the store only estimates (a mailbox's server's) is read whole then, so that its file's length,
/// and the size the Markdown gives, are its content's.
/// </summary>
internal sealed class ItemFiles
{
    private ItemFiles(MailItem item, IReadOnlyList<ItemFile> files, IReadOnlyList<ItemFile> attachments)
    {
        Item = item;
        Files = files;
        Attachments = attachments;
    }

    public MailItem Item { get; }

    /// <summary>The files in the item's folder itself.</summary>
    public IReadOnlyList<ItemFile> Files { get; }

    /// <summary>The files in its attachments folder; none means no such folder.</summary>
    public IReadOnlyList<ItemFile> Attachments { get; }

    public bool HasAttachmentsFolder => Attachments.Count > 0;

    public DateTimeOffset? Modified => Item.Summary.Date ?? Item.Modified;

    public DateTimeOffset? Created => Item.Created ?? Item.Summary.Date;

    public ItemFile? File(string name) => Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public ItemFile? Attachment(string name) => Attachments.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public VfsEntry EntryOf(ItemFile file) => VfsEntry.File(file.Name, file.Length, Modified, Created);

    public VfsEntry AttachmentsEntry => VfsEntry.Directory(MailNames.AttachmentsFolder, Modified, Created);

    /// <param name="folderName">The item's folder name: the stem of its .md and .eml.</param>
    public static async Task<ItemFiles> BuildAsync(MailItem item, string folderName, CancellationToken ct)
    {
        // the attachments' names, fixed first so the Markdown's links, the .eml's parts and the attachments folder agree;
        // a contact's (its photo) are the vCard's business alone
        var named = AttachmentNames.For(item.Kind == MailItemKind.Contact ? item.Attachments : await SettleAsync(item.Attachments, ct).ConfigureAwait(false));
        var files = new List<ItemFile>();
        switch (item.Kind)
        {
            case MailItemKind.Appointment:
                files.Add(Made(MailNames.EventMarkdown, MessageMarkdown.Build(item, named), "an appointment as Markdown"));
                files.Add(Made(MailNames.EventCalendar, CalendarFile.Build(item), "an appointment as iCalendar"));
                break;
            case MailItemKind.Contact:
                files.Add(Made(folderName, await ContactFile.BuildAsync(item, ct).ConfigureAwait(false), "a contact as a vCard"));
                break;
            case MailItemKind.Message:
                files.Add(Made(folderName + ".md", MessageMarkdown.Build(item, named), "a message as Markdown"));
                files.Add(Made(folderName + ".eml", item.RawMime ?? MimeWriter.Build(item), "a message as .eml"));
                break;
            default:
                files.Add(Made(folderName + ".md", MessageMarkdown.Build(item, named), "an item as Markdown"));
                break;
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

        return new ItemFiles(item, files, attachments);
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
