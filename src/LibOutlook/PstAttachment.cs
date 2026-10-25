using System.Buffers.Binary;
using LibOutlook.Ltp;
using LibOutlook.Ndb;

namespace LibOutlook;

/// <summary>How an attachment is held (PidTagAttachMethod).</summary>
public enum PstAttachMethod
{
    /// <summary>A placeholder with no content.</summary>
    None = 0,

    /// <summary>The content is in the file.</summary>
    ByValue = 1,

    /// <summary>A path to the file elsewhere.</summary>
    ByReference = 2,

    /// <summary>A path to resolve.</summary>
    ByReferenceResolve = 3,

    /// <summary>A path only.</summary>
    ByReferenceOnly = 4,

    /// <summary>Another message, in the file: see <see cref="PstAttachment.EmbeddedMessage"/>.</summary>
    EmbeddedMessage = 5,

    /// <summary>An OLE document, in the file.</summary>
    Ole = 6,

    /// <summary>A reference to a file on the web (a cloud attachment).</summary>
    ByWebReference = 7,
}

/// <summary>An attachment ([MS-PST] 2.4.6): a file, an inline image, or a whole message attached to another.</summary>
public sealed class PstAttachment
{
    private readonly PstFile _file;
    private readonly NodeContext _node;
    private readonly Lazy<PropertyContext> _context;
    private readonly Lazy<PstMessage?> _embedded;

    internal PstAttachment(PstFile file, NodeContext node, TableRow? summary)
    {
        _file = file;
        _node = node;
        _context = new Lazy<PropertyContext>(() => new PropertyContext(_node));
        Properties = new PstProperties(file, new RowBackedSource(summary, _context));
        _embedded = new Lazy<PstMessage?>(ReadEmbeddedMessage);
    }

    /// <summary>The attachment's node id within its message.</summary>
    public uint Nid => _node.Node.Nid;

    /// <summary>All of the attachment's properties.</summary>
    public PstProperties Properties { get; }

    /// <summary>How the attachment is held.</summary>
    public PstAttachMethod Method => (PstAttachMethod)(Properties.GetInt32(PstPropertyIds.AttachMethod) ?? 0);

    /// <summary>The file name: the long one, else the short one, else the display name; null when the attachment has none.</summary>
    public string? FileName => LongFileName ?? ShortFileName ?? DisplayName;

    /// <summary>PidTagAttachLongFilename.</summary>
    public string? LongFileName => Properties.GetString(PstPropertyIds.AttachLongFilename);

    /// <summary>PidTagAttachFilename, the 8.3 name.</summary>
    public string? ShortFileName => Properties.GetString(PstPropertyIds.AttachFilename);

    /// <summary>PidTagDisplayName.</summary>
    public string? DisplayName => Properties.GetString(PstPropertyIds.DisplayName);

    /// <summary>The extension with its dot, when stored.</summary>
    public string? Extension => Properties.GetString(PstPropertyIds.AttachExtension);

    /// <summary>The content type, when stored.</summary>
    public string? MimeTag => Properties.GetString(PstPropertyIds.AttachMimeTag);

    /// <summary>What an HTML body's cid: references name, for an inline image.</summary>
    public string? ContentId => Properties.GetString(PstPropertyIds.AttachContentId);

    /// <summary>PidTagAttachContentLocation.</summary>
    public string? ContentLocation => Properties.GetString(PstPropertyIds.AttachContentLocation);

    /// <summary>The path, for an attachment held by reference.</summary>
    public string? PathName => Properties.GetString(PstPropertyIds.AttachLongPathname) ?? Properties.GetString(PstPropertyIds.AttachPathname);

    /// <summary>Whether Outlook hides the attachment from the list (an inline image, say).</summary>
    public bool IsHidden => Properties.GetBoolean(PstPropertyIds.AttachmentHidden) ?? false;

    /// <summary>PidTagAttachFlags: 0x1 invisible in HTML, 0x2 invisible in RTF, 0x4 rendered in the body.</summary>
    public int Flags => Properties.GetInt32(PstPropertyIds.AttachFlags) ?? 0;

    /// <summary>Where in the body the attachment is rendered; -1 when it is not.</summary>
    public int RenderingPosition => Properties.GetInt32(PstPropertyIds.RenderingPosition) ?? -1;

    /// <summary>
    /// Whether the attachment holds a modified occurrence of a recurring series (PidTagAttachmentFlags afException,
    /// [MS-OXOCAL] 2.2.10.1): an embedded message Outlook keeps for the series, never listed as an attachment.
    /// </summary>
    public bool IsRecurrenceException => ((Properties.GetInt32(PstPropertyIds.AttachmentFlags) ?? 0) & 0x2) != 0;

    /// <summary>Whether the attachment is an image the HTML body shows in place rather than a file to list.</summary>
    public bool IsInline => ContentId is not null && (IsHidden || (Flags & 0x4) != 0);

    /// <summary>The content's size in bytes when it is in the file, else what the store recorded (PidTagAttachSize).</summary>
    public long Size
    {
        get
        {
            if (Method is PstAttachMethod.ByValue or PstAttachMethod.Ole)
            {
                using var content = OpenContent();
                return content.Length;
            }

            return Properties.GetInt64(PstPropertyIds.AttachSize) ?? 0;
        }
    }

    /// <summary>When the attachment was made (UTC), when stored.</summary>
    public DateTime? CreationTime => Properties.GetDateTime(PstPropertyIds.CreationTime);

    /// <summary>When the attachment last changed (UTC), when stored.</summary>
    public DateTime? LastModificationTime => Properties.GetDateTime(PstPropertyIds.LastModificationTime);

    /// <summary>The message attached, when <see cref="Method"/> is <see cref="PstAttachMethod.EmbeddedMessage"/>; else null.</summary>
    public PstMessage? EmbeddedMessage => _embedded.Value;

    /// <summary>
    /// The content as a read-only, seekable stream; empty for an attachment held elsewhere or an embedded message.
    /// Large contents are read block by block as the stream is read.
    /// </summary>
    public Stream OpenContent()
    {
        if (!_context.Value.TryGetReference(PstPropertyIds.AttachData, out var type, out var hnid) || hnid == 0)
        {
            return Stream.Null;
        }

        if (type == (ushort)PstPropertyType.Object)
        {
            // an OLE document's bytes: the object reference names the subnode holding them (an embedded message is one too, see EmbeddedMessage)
            if (Method == PstAttachMethod.EmbeddedMessage || ObjectSubnode(hnid) is not { } obj)
            {
                return Stream.Null;
            }

            return new NodeDataStream(_file.Blocks, obj.BidData);
        }

        if ((hnid & 0x1F) == 0)
        {
            return new MemoryStream(_context.Value.Bytes(hnid), writable: false);
        }

        var data = _node.Subnode(hnid) ?? throw new PstException($"attachment {Nid:X}: its content is in subnode {hnid:X}, which it has not");
        return new NodeDataStream(_file.Blocks, data.BidData);
    }

    /// <summary>The whole content; empty for an attachment held elsewhere or an embedded message.</summary>
    public byte[] GetContent()
    {
        using var content = OpenContent();
        var bytes = new byte[content.Length];
        content.ReadExactly(bytes);
        return bytes;
    }

    /// <inheritdoc/>
    public override string ToString() => FileName ?? $"attachment {Nid:X}";

    private PstMessage? ReadEmbeddedMessage()
    {
        if (Method != PstAttachMethod.EmbeddedMessage
            || !_context.Value.TryGetReference(PstPropertyIds.AttachData, out var type, out var hnid)
            || type != (ushort)PstPropertyType.Object
            || ObjectSubnode(hnid) is not { } message)
        {
            return null;
        }

        return new PstMessage(_file, _file.Context(message, _node), null);
    }

    /// <summary>The subnode a PtypObject value names: its HNID leads to 8 bytes, the subnode's NID then the object's size.</summary>
    private NodeEntry? ObjectSubnode(uint hnid)
    {
        var reference = _context.Value.Bytes(hnid);
        if (reference.Length < 4)
        {
            return null;
        }

        return _node.Subnode(BinaryPrimitives.ReadUInt32LittleEndian(reference));
    }
}
