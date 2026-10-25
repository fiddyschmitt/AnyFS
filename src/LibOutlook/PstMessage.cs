using LibOutlook.Ltp;

namespace LibOutlook;

/// <summary>
/// A message ([MS-PST] 2.4.5): a mail, contact, appointment, task or note, told apart by <see cref="MessageClass"/>.
/// The common properties are named here; the rest, named properties included, are in <see cref="Properties"/>.
/// </summary>
public sealed class PstMessage
{
    private readonly PstFile _file;
    private readonly NodeContext _node;
    private readonly Lazy<PropertyContext> _context;
    private readonly Lazy<IReadOnlyList<PstRecipient>> _recipients;
    private readonly Lazy<IReadOnlyList<PstAttachment>> _attachments;
    private readonly TableRow? _summary;

    // listedOnly: properties from the summary row's cells alone, the message's own node never read
    internal PstMessage(PstFile file, NodeContext node, TableRow? summary, bool listedOnly = false)
    {
        _file = file;
        _node = node;
        _summary = summary;
        _context = new Lazy<PropertyContext>(() => new PropertyContext(_node));
        Properties = new PstProperties(file, listedOnly && summary is not null ? summary : new RowBackedSource(summary, _context));
        _recipients = new Lazy<IReadOnlyList<PstRecipient>>(ReadRecipients);
        _attachments = new Lazy<IReadOnlyList<PstAttachment>>(ReadAttachments);
    }

    /// <summary>The message's node id, stable for the file's life (an embedded message's is its subnode's).</summary>
    public uint Nid => _node.Node.Nid;

    /// <summary>
    /// The message as its folder's contents table lists it: the same members, with properties from the table's cells
    /// alone, the message itself not read (for a message whose own properties cannot be read: what the folder lists of
    /// it, as Outlook's list shows it). Null for a message not listed from a folder (an embedded one, or one found by id).
    /// </summary>
    public PstMessage? AsListed() => _summary is null ? null : new PstMessage(_file, _node, _summary, listedOnly: true);

    /// <summary>All of the message's properties.</summary>
    public PstProperties Properties { get; }

    /// <summary>The subject, prefix included ("RE: Figures"); empty when the message has none.</summary>
    public string Subject
    {
        get
        {
            var s = Properties.GetString(PstPropertyIds.Subject);
            if (s is null)
            {
                return "";
            }

            // a PST marks a subject with a prefix by a leading 0x01 and the prefix's length ([MS-PST] 2.5.3.1.1.1)
            return s.Length >= 2 && s[0] == '\u0001' ? s[2..] : s;
        }
    }

    /// <summary>The subject without its prefix, when stored.</summary>
    public string? NormalizedSubject => Properties.GetString(PstPropertyIds.NormalizedSubject);

    /// <summary>The conversation's topic, when stored.</summary>
    public string? ConversationTopic => Properties.GetString(PstPropertyIds.ConversationTopic);

    /// <summary>"IPM.Note" for mail, "IPM.Contact", "IPM.Appointment", "IPM.Task", "IPM.StickyNote", "IPM.Schedule.Meeting.Request" and so on; empty when unset.</summary>
    public string MessageClass => Properties.GetString(PstPropertyIds.MessageClass) ?? "";

    /// <summary>The sender's name.</summary>
    public string? SenderName => Properties.GetString(PstPropertyIds.SenderName);

    /// <summary>The sender's address (SMTP, or an Exchange legacy DN when <see cref="SenderAddressType"/> is "EX").</summary>
    public string? SenderEmailAddress => Properties.GetString(PstPropertyIds.SenderEmailAddress);

    /// <summary>"SMTP" or "EX".</summary>
    public string? SenderAddressType => Properties.GetString(PstPropertyIds.SenderAddressType);

    /// <summary>Who the message is shown as from, when sent on someone's behalf.</summary>
    public string? SentRepresentingName => Properties.GetString(PstPropertyIds.SentRepresentingName);

    /// <summary>The address the message is shown as from.</summary>
    public string? SentRepresentingEmailAddress => Properties.GetString(PstPropertyIds.SentRepresentingEmailAddress);

    /// <summary>The To line as displayed, names separated by semicolons.</summary>
    public string? DisplayTo => Properties.GetString(PstPropertyIds.DisplayTo);

    /// <summary>The Cc line as displayed.</summary>
    public string? DisplayCc => Properties.GetString(PstPropertyIds.DisplayCc);

    /// <summary>The Bcc line as displayed.</summary>
    public string? DisplayBcc => Properties.GetString(PstPropertyIds.DisplayBcc);

    /// <summary>When the message was sent (UTC).</summary>
    public DateTime? ClientSubmitTime => Properties.GetDateTime(PstPropertyIds.ClientSubmitTime);

    /// <summary>When the message arrived (UTC).</summary>
    public DateTime? MessageDeliveryTime => Properties.GetDateTime(PstPropertyIds.MessageDeliveryTime);

    /// <summary>When the item was made (UTC).</summary>
    public DateTime? CreationTime => Properties.GetDateTime(PstPropertyIds.CreationTime);

    /// <summary>When the item last changed (UTC).</summary>
    public DateTime? LastModificationTime => Properties.GetDateTime(PstPropertyIds.LastModificationTime);

    /// <summary>PidTagMessageFlags: 0x1 read, 0x8 unsent, 0x10 has attachments, 0x40 from me.</summary>
    public int Flags => Properties.GetInt32(PstPropertyIds.MessageFlags) ?? 0;

    /// <summary>Whether the message has been read.</summary>
    public bool IsRead => (Flags & 0x1) != 0;

    /// <summary>Whether the message is a draft, never sent.</summary>
    public bool IsUnsent => (Flags & 0x8) != 0;

    /// <summary>Whether the message has attachments (inline images count): PidTagMessageFlags says, a folder's table holding it; PidTagHasAttachments where the flags are not stored.</summary>
    public bool HasAttachments => Properties.GetInt32(PstPropertyIds.MessageFlags) is { } flags ? (flags & 0x10) != 0 : Properties.GetBoolean(PstPropertyIds.HasAttachments) == true;

    /// <summary>The message's size in bytes as the store counts it.</summary>
    public long Size => Properties.GetInt64(PstPropertyIds.MessageSize) ?? 0;

    /// <summary>0 low, 1 normal, 2 high; null when unset.</summary>
    public int? Importance => Properties.GetInt32(PstPropertyIds.Importance);

    /// <summary>0 normal, 1 personal, 2 private, 3 confidential; null when unset.</summary>
    public int? Sensitivity => Properties.GetInt32(PstPropertyIds.Sensitivity);

    /// <summary>The Message-ID header.</summary>
    public string? InternetMessageId => Properties.GetString(PstPropertyIds.InternetMessageId);

    /// <summary>The Internet headers as received, for a message that came in over SMTP.</summary>
    public string? TransportMessageHeaders => Properties.GetString(PstPropertyIds.TransportMessageHeaders);

    /// <summary>The code page of the HTML body, when stored.</summary>
    public int? InternetCodepage => Properties.GetInt32(PstPropertyIds.InternetCodepage);

    /// <summary>The plain-text body, when stored.</summary>
    public string? Body => Properties.GetString(PstPropertyIds.Body);

    /// <summary>The HTML body, decoded by <see cref="InternetCodepage"/>, when stored.</summary>
    public string? BodyHtml => Properties.Get(PstPropertyIds.Html)?.Value switch
    {
        string s => s,
        byte[] b => PropertyValues.DecodeString8(b, InternetCodepage ?? Properties.CodePage),
        _ => null,
    };

    /// <summary>The HTML body's bytes as stored, when stored.</summary>
    public byte[]? BodyHtmlBytes => Properties.Get(PstPropertyIds.Html)?.Value switch
    {
        byte[] b => b,
        string s => PropertyValues.EncodingFor(InternetCodepage ?? Properties.CodePage).GetBytes(s),
        _ => null,
    };

    /// <summary>The RTF body, decompressed, when stored.</summary>
    public string? BodyRtf
    {
        get
        {
            var compressed = Properties.GetBinary(PstPropertyIds.RtfCompressed);
            return compressed is null ? null : RtfCompression.DecompressToString(compressed);
        }
    }

    /// <summary>The recipients, in the order stored.</summary>
    public IReadOnlyList<PstRecipient> Recipients => _recipients.Value;

    /// <summary>The attachments, in the order stored; inline images among them.</summary>
    public IReadOnlyList<PstAttachment> Attachments => _attachments.Value;

    /// <inheritdoc/>
    public override string ToString() => Subject;

    private IReadOnlyList<PstRecipient> ReadRecipients()
    {
        var sub = _node.Subnode(NodeId.RecipientTable);
        var table = sub is null ? null : TableContext.TryOpen(_file.Context(sub.Value, _node));
        return table is null ? [] : table.Rows().Select(row => new PstRecipient(_file, row)).ToList();
    }

    private IReadOnlyList<PstAttachment> ReadAttachments()
    {
        var sub = _node.Subnode(NodeId.AttachmentTable);
        var table = sub is null ? null : TableContext.TryOpen(_file.Context(sub.Value, _node));
        if (table is null)
        {
            return [];
        }

        var list = new List<PstAttachment>(table.RowCount);
        foreach (var row in table.Rows())
        {
            if (_node.Subnode(row.RowId) is { } attachment && NodeId.TypeOf(attachment.Nid) == NodeId.TypeAttachment)
            {
                list.Add(new PstAttachment(_file, _file.Context(attachment, _node), row));
            }
        }

        return list;
    }
}
