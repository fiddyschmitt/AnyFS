using LibOutlook.Ltp;
using LibOutlook.Ndb;

namespace LibOutlook;

/// <summary>A folder ([MS-PST] 2.4.4): its properties, its subfolders and its messages, each read when first asked for.</summary>
public sealed class PstFolder
{
    private readonly PstFile _file;
    private readonly NodeContext _node;
    private readonly Lazy<PropertyContext> _context;
    private readonly Lazy<IReadOnlyList<PstFolder>> _folders;

    internal PstFolder(PstFile file, NodeEntry node, TableRow? summary)
    {
        _file = file;
        _node = file.Context(node);
        _context = new Lazy<PropertyContext>(() => new PropertyContext(_node));
        Properties = new PstProperties(file, new RowBackedSource(summary, _context));
        _folders = new Lazy<IReadOnlyList<PstFolder>>(ReadFolders);
    }

    /// <summary>The folder's node id, stable for the file's life.</summary>
    public uint Nid => _node.Node.Nid;

    /// <summary>All of the folder's properties.</summary>
    public PstProperties Properties { get; }

    /// <summary>The folder's name.</summary>
    public string Name => Properties.GetString(PstPropertyIds.DisplayName) ?? "";

    /// <summary>How many messages the folder holds, as the folder states it.</summary>
    public int ContentCount => Properties.GetInt32(PstPropertyIds.ContentCount) ?? 0;

    /// <summary>How many of them are unread.</summary>
    public int UnreadCount => Properties.GetInt32(PstPropertyIds.ContentUnreadCount) ?? 0;

    /// <summary>How many hidden (associated) items the folder holds: views, rules, Outlook's own records.</summary>
    public int AssociatedContentCount => Properties.GetInt32(PstPropertyIds.AssociatedContentCount) ?? 0;

    /// <summary>Whether the folder has subfolders, as the folder states it.</summary>
    public bool HasSubfolders => Properties.GetBoolean(PstPropertyIds.Subfolders) ?? false;

    /// <summary>What the folder holds: "IPF.Note" mail, "IPF.Contact", "IPF.Appointment", "IPF.Task", "IPF.StickyNote", "IPF.Journal"; null when unset.</summary>
    public string? ContainerClass => Properties.GetString(PstPropertyIds.ContainerClass);

    /// <summary>When the folder was made (UTC), when known.</summary>
    public DateTime? CreationTime => Properties.GetDateTime(PstPropertyIds.CreationTime);

    /// <summary>When the folder last changed (UTC), when known.</summary>
    public DateTime? LastModificationTime => Properties.GetDateTime(PstPropertyIds.LastModificationTime);

    /// <summary>Whether this is a search folder: its messages live in other folders.</summary>
    public bool IsSearchFolder => NodeId.TypeOf(Nid) == NodeId.TypeSearchFolder;

    /// <summary>The subfolders, in the order the file keeps them.</summary>
    public IReadOnlyList<PstFolder> Folders => _folders.Value;

    /// <summary>The messages (and contacts, appointments, tasks: every item the folder shows), read as enumerated.</summary>
    public IEnumerable<PstMessage> Messages => Items(IsSearchFolder ? NodeId.TypeSearchContentsTable : NodeId.TypeContentsTable);

    /// <summary>The hidden (associated) items: views, rules, the records Outlook keeps for itself.</summary>
    public IEnumerable<PstMessage> AssociatedMessages => Items(NodeId.TypeAssocContentsTable);

    /// <summary>The subfolder with this name (case ignored), or null.</summary>
    public PstFolder? FindFolder(string name) => Folders.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every folder below this one, depth first, each before its children.</summary>
    public IEnumerable<PstFolder> Descendants()
    {
        foreach (var child in Folders)
        {
            yield return child;
            foreach (var below in child.Descendants())
            {
                yield return below;
            }
        }
    }

    /// <inheritdoc/>
    public override string ToString() => Name;

    private IReadOnlyList<PstFolder> ReadFolders()
    {
        var table = Table(NodeId.TypeHierarchyTable);
        if (table is null)
        {
            return [];
        }

        var list = new List<PstFolder>(table.RowCount);
        foreach (var row in table.Rows())
        {
            if (_file.FindNode(row.RowId) is { } child && NodeId.IsFolder(child.Nid))
            {
                list.Add(new PstFolder(_file, child, row));
            }
        }

        return list;
    }

    private IEnumerable<PstMessage> Items(uint tableType)
    {
        var table = Table(tableType);
        if (table is null)
        {
            yield break;
        }

        foreach (var row in table.Rows())
        {
            if (_file.FindNode(row.RowId) is { } node && NodeId.IsMessage(node.Nid))
            {
                yield return new PstMessage(_file, _file.Context(node), row);
            }
        }
    }

    private TableContext? Table(uint type)
    {
        var node = _file.FindNode(NodeId.WithType(Nid, type));
        return node is null ? null : TableContext.TryOpen(_file.Context(node.Value), OpenRow);
    }

    /// <summary>A row's own properties (an item's, a subfolder's), for a table to place its column stores by; null when the row is no node, or does not read.</summary>
    private IPropertySource? OpenRow(uint nid)
    {
        try
        {
            return _file.FindNode(nid) is { } node ? new PropertyContext(_file.Context(node)) : null;
        }
        catch (PstException)
        {
            return null;
        }
    }
}
