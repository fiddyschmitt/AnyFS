using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using LibOutlook.Ltp;
using LibOutlook.Ndb;

namespace LibOutlook;

/// <summary>How a file is opened.</summary>
public sealed class PstOpenOptions
{
    /// <summary>
    /// The code page for 8-bit strings of items that do not say their own (PidTagMessageCodepage): 1252, Western
    /// European, unless the file is known to come from elsewhere. Unicode files hold few such strings.
    /// </summary>
    public int AnsiCodePage { get; init; } = 1252;
}

/// <summary>The folders the message store points at ([MS-PST] 2.4.3.1).</summary>
public enum PstSpecialFolder
{
    /// <summary>The top of the user's tree ("Top of Outlook data file", a mailbox's IPM_SUBTREE).</summary>
    TopOfStore,

    /// <summary>The Outbox.</summary>
    Outbox,

    /// <summary>Deleted Items.</summary>
    DeletedItems,

    /// <summary>Sent Items.</summary>
    SentItems,

    /// <summary>The root of the search folders.</summary>
    SearchRoot,

    /// <summary>The Views folder.</summary>
    Views,

    /// <summary>The Common Views folder.</summary>
    CommonViews,
}

/// <summary>
/// An Outlook data file, PST or OST ([MS-PST]), open for reading. A path is read through a memory-mapped view with full
/// sharing, so a file Outlook has open can be read as well; a stream is read as it is. Everything is read on demand
/// and checked (page and block checksums, signatures, back pointers) on the way; what does not add up raises a
/// <see cref="PstException"/>. Instances are safe to read from several threads at once.
/// </summary>
public sealed class PstFile : IDisposable
{
    private readonly IDataSource _source;
    private readonly object _gate = new();
    private Header _header;
    private NodeDatabase _ndb;
    private BlockStore _blocks;
    private Lazy<PstProperties?> _store;
    private Lazy<PstNamedProperties> _named;
    private Lazy<PstFolder> _root;
    private Lazy<PstFolder> _top;

    private PstFile(IDataSource source, PstOpenOptions options)
    {
        _source = source;
        AnsiCodePage = options.AnsiCodePage;
        _header = ReadHeader(source);
        _ndb = new NodeDatabase(source, _header);
        _blocks = new BlockStore(_ndb);
        Reset();
    }

    /// <summary>Opens the file at a path, shared with whoever else has it open (Outlook included).</summary>
    /// <exception cref="PstException">The file is not an Outlook data file, or one of a version this library does not read.</exception>
    public static PstFile Open(string path, PstOpenOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var source = new MappedFileData(path);
        try
        {
            return new PstFile(source, options ?? new PstOpenOptions());
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>Opens a file held in a readable, seekable stream.</summary>
    /// <exception cref="PstException">The stream is not an Outlook data file, or one of a version this library does not read.</exception>
    public static PstFile Open(Stream stream, bool leaveOpen = false, PstOpenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var source = new StreamData(stream, ownsStream: !leaveOpen);
        try
        {
            return new PstFile(source, options ?? new PstOpenOptions());
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>The on-disk format.</summary>
    public PstFormat Format => _header.Layout.Format;

    /// <summary>How the file's data blocks are obfuscated.</summary>
    public PstEncoding Encoding => _header.Encoding;

    /// <summary>Whether the file is an OST (a mailbox's offline copy) rather than a PST.</summary>
    public bool IsOst => _header.IsOst;

    /// <summary>The format version from the header (wVer): 14 or 15 ANSI, 23 Unicode, 36 and up the 4 KiB-page OST.</summary>
    public int Version => _header.Version;

    /// <summary>The code page 8-bit strings fall back to.</summary>
    public int AnsiCodePage { get; }

    /// <summary>The file's size as its header states it (a live OST is often larger on disk, pre-allocated).</summary>
    public long Length => (long)_header.CbFile;

    /// <summary>The message store's properties (its name, the entry ids of its special folders), or null for a file without one.</summary>
    public PstProperties? MessageStore => _store.Value;

    /// <summary>The store's display name ("Outlook Data File", the mailbox's address), or null.</summary>
    public string? DisplayName => MessageStore?.GetString(PstPropertyIds.DisplayName);

    /// <summary>The root folder (NID 0x122), parent of the user's tree and of the file's internal folders.</summary>
    public PstFolder RootFolder => _root.Value;

    /// <summary>
    /// The top of the user's tree: the folder the message store names as such (a PST's "Top of Outlook data file"); in
    /// an OST, whose store names none, the mailbox's IPM_SUBTREE under "Root - Mailbox"; the root when neither is there.
    /// </summary>
    public PstFolder TopFolder => _top.Value;

    /// <summary>The file's named properties.</summary>
    public PstNamedProperties NamedProperties => _named.Value;

    /// <summary>A folder the message store points at, or null when it does not.</summary>
    public PstFolder? GetSpecialFolder(PstSpecialFolder which)
    {
        var id = which switch
        {
            PstSpecialFolder.TopOfStore => PstPropertyIds.IpmSubTreeEntryId,
            PstSpecialFolder.Outbox => PstPropertyIds.IpmOutboxEntryId,
            PstSpecialFolder.DeletedItems => PstPropertyIds.IpmWastebasketEntryId,
            PstSpecialFolder.SentItems => PstPropertyIds.IpmSentMailEntryId,
            PstSpecialFolder.SearchRoot => PstPropertyIds.FinderEntryId,
            PstSpecialFolder.Views => PstPropertyIds.ViewsEntryId,
            PstSpecialFolder.CommonViews => PstPropertyIds.CommonViewsEntryId,
            _ => throw new ArgumentOutOfRangeException(nameof(which)),
        };
        var entryId = MessageStore?.GetBinary(id);
        return entryId is { Length: >= 24 } ? GetFolder(BinaryPrimitives.ReadUInt32LittleEndian(entryId.AsSpan(20))) : null;
    }

    /// <summary>The folder with this NID, or null when the file has no such folder.</summary>
    public PstFolder? GetFolder(uint nid)
    {
        if (!NodeId.IsFolder(nid))
        {
            return null;
        }

        var node = _ndb.FindNode(nid);
        return node is null ? null : new PstFolder(this, node.Value, null);
    }

    /// <summary>The message with this NID, or null when the file has no such message.</summary>
    public PstMessage? GetMessage(uint nid)
    {
        if (!NodeId.IsMessage(nid))
        {
            return null;
        }

        var node = _ndb.FindNode(nid);
        return node is null ? null : new PstMessage(this, Context(node.Value), null);
    }

    /// <summary>Every folder, the root first, each before its children.</summary>
    public IEnumerable<PstFolder> EnumerateFolders()
    {
        yield return RootFolder;
        foreach (var folder in RootFolder.Descendants())
        {
            yield return folder;
        }
    }

    /// <summary>
    /// Takes in changes to a file another program is writing (Outlook's OST): the header is read again and everything
    /// cached is dropped. Folders and messages obtained before go on reading the file as it was; get them again.
    /// </summary>
    public void Refresh()
    {
        lock (_gate)
        {
            _source.Refresh();
            _header = ReadHeader(_source);
            _ndb = new NodeDatabase(_source, _header);
            _blocks = new BlockStore(_ndb);
            Reset();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _source.Dispose();

    internal BlockStore Blocks => _blocks;

    internal NodeEntry? FindNode(uint nid) => _ndb.FindNode(nid);

    internal NodeContext Context(NodeEntry node) => new(_blocks, node);

    [MemberNotNull(nameof(_store), nameof(_named), nameof(_root), nameof(_top))]
    private void Reset()
    {
        _store = new Lazy<PstProperties?>(() =>
        {
            var node = _ndb.FindNode(NodeId.MessageStore);
            return node is null ? null : new PstProperties(this, new PropertyContext(Context(node.Value)));
        });
        _named = new Lazy<PstNamedProperties>(() => new PstNamedProperties(this));
        _root = new Lazy<PstFolder>(() => GetFolder(NodeId.RootFolder) ?? throw new PstException("the file has no root folder (node 0x122)"));
        _top = new Lazy<PstFolder>(FindTopFolder);
    }

    private PstFolder FindTopFolder()
    {
        if (GetSpecialFolder(PstSpecialFolder.TopOfStore) is { } named)
        {
            return named;
        }

        // an OST: the mailbox root's IPM_SUBTREE (the public folders' root has one too, never the user's)
        var root = RootFolder;
        var mailbox = root.Folders.FirstOrDefault(f => f.Name.Equals("Root - Mailbox", StringComparison.OrdinalIgnoreCase));
        var subtree = mailbox?.FindFolder("IPM_SUBTREE")
            ?? root.Folders.Where(f => !f.Name.StartsWith("Root - Public", StringComparison.OrdinalIgnoreCase)).Select(f => f.FindFolder("IPM_SUBTREE")).FirstOrDefault(f => f is not null)
            ?? root.FindFolder("IPM_SUBTREE");
        return subtree ?? root;
    }

    private static Header ReadHeader(IDataSource source)
    {
        var size = (int)Math.Min(Header.Size, source.Length);
        return Header.Parse(source.Read(0, size));
    }
}
