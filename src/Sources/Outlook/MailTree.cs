using AnyFS.Sdk;
using AnyFS.Sources.Outlook.Mail;

namespace AnyFS.Sources.Outlook;

/// <summary>A node of the tree an Outlook source serves: a folder, an item's folder, or a contact's file.</summary>
internal abstract class TreeNode(string name)
{
    public string Name { get; } = name;

    public abstract VfsEntry Entry { get; }
}

/// <summary>A folder: one of the mailbox's, a year, a month. Children are looked up in either case.</summary>
internal sealed class DirNode(string name, MailFolder? folder, DateTimeOffset? created, DateTimeOffset? modified) : TreeNode(name)
{
    private readonly List<TreeNode> _children = [];
    private readonly Dictionary<string, TreeNode> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mailbox folder this is; null for a year or month folder.</summary>
    public MailFolder? Folder => folder;

    public DateTimeOffset? Created { get; private set; } = created;

    public DateTimeOffset? Modified { get; private set; } = modified;

    public IReadOnlyList<TreeNode> Children => _children;

    public override VfsEntry Entry => VfsEntry.Directory(Name, Modified, Created);

    public TreeNode? Child(string name) => _byName.TryGetValue(name, out var node) ? node : null;

    public bool Has(string name) => _byName.ContainsKey(name);

    public void Add(TreeNode node)
    {
        _children.Add(node);
        _byName[node.Name] = node;
    }

    /// <summary>The year or month folder of this name, made when first needed.</summary>
    public DirNode GetOrAddPeriod(string name)
    {
        if (_byName.TryGetValue(name, out var existing) && existing is DirNode { Folder: null } period)
        {
            return period;
        }

        var dir = new DirNode(Unique(name, isFile: false), null, null, null);
        Add(dir);
        return dir;
    }

    /// <summary>Widens the folder's dates to take in an item's: a month is as new as its newest item.</summary>
    public void Touch(DateTimeOffset? date)
    {
        if (date is not { } d)
        {
            return;
        }

        if (Modified is null || d > Modified)
        {
            Modified = d;
        }

        if (Created is null || d < Created)
        {
            Created = d;
        }
    }

    /// <summary>The name, or the name numbered ("name (2)", "report (2).pdf") when a child has it already.</summary>
    public string Unique(string name, bool isFile)
    {
        if (!_byName.ContainsKey(name))
        {
            return name;
        }

        for (var n = 2; ; n++)
        {
            var candidate = isFile ? MailNames.NumberedFile(name, n) : MailNames.NumberedFolder(name, n);
            if (!_byName.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }
}

/// <summary>An item's folder: the message (or appointment, task, note) with its files inside, made when the folder is opened.</summary>
internal sealed class ItemNode(string name, MailItemSummary item) : TreeNode(name)
{
    public MailItemSummary Item => item;

    public override VfsEntry Entry => VfsEntry.Directory(Name, item.Date ?? item.Modified, item.Created ?? item.Date);
}

/// <summary>A contact: one .vcf file, made when it is listed or opened.</summary>
internal sealed class ContactNode(string name, MailItemSummary item) : TreeNode(name)
{
    public MailItemSummary Item => item;

    /// <summary>The entry before the card is made: its size is a placeholder until then (see <see cref="OutlookSource"/>).</summary>
    public override VfsEntry Entry => VfsEntry.File(Name, 0, item.Modified ?? item.Date, item.Created);
}

/// <summary>
/// The tree an Outlook source serves (spec 7.7): the mailbox's folders as they are; under each, its items by year and
/// month, a folder per message named by date, sender and subject; contacts as .vcf files in their folder. Immutable:
/// a refresh builds a new tree and the source swaps it in whole. Items are named from their listing cells alone; the
/// store opens none while the tree is built.
/// </summary>
internal sealed class MailTree
{
    private MailTree(DirNode root, long folders, long items, long files)
    {
        Root = root;
        FolderCount = folders;
        ItemCount = items;
        FileCount = files;
    }

    public static MailTree Empty { get; } = new(new DirNode("", null, null, null), 0, 0, 0);

    public DirNode Root { get; }

    /// <summary>The folders, year and month folders and item folders.</summary>
    public long FolderCount { get; }

    public long ItemCount { get; }

    /// <summary>The files the items make (two per message, two per appointment, one per contact, one per other item), attachments not counted.</summary>
    public long FileCount { get; }

    public static MailTree Build(IMailStore store)
    {
        var root = new DirNode("", null, null, null);
        AddFolders(root, store.Folders, store);
        var counts = new Counts();
        Count(root, counts);
        return new MailTree(root, counts.Folders, counts.Items, counts.Files);
    }

    /// <summary>What the tree holds: every folder (the mailbox's, years, months, items' folders) and the files the items make.</summary>
    private static void Count(DirNode dir, Counts counts)
    {
        foreach (var child in dir.Children)
        {
            switch (child)
            {
                case DirNode sub:
                    counts.Folders++;
                    Count(sub, counts);
                    break;
                case ItemNode item:
                    counts.Folders++;
                    counts.Items++;
                    counts.Files += item.Item.Kind is MailItemKind.Message or MailItemKind.Appointment ? 2 : 1;
                    break;
                case ContactNode:
                    counts.Items++;
                    counts.Files++;
                    break;
            }
        }
    }

    /// <summary>The node a path names and the segments left after it (inside an item's folder), or null when no node is on the way.</summary>
    public (TreeNode Node, ArraySegment<string> Inside)? Resolve(string normalizedPath)
    {
        var segments = VfsPath.Split(normalizedPath);
        TreeNode node = Root;
        var i = 0;
        for (; i < segments.Length; i++)
        {
            if (node is not DirNode dir)
            {
                break;
            }

            var next = dir.Child(segments[i]);
            if (next is null)
            {
                return null;
            }

            node = next;
        }

        return (node, new ArraySegment<string>(segments, i, segments.Length - i));
    }

    private static void AddFolders(DirNode parent, IReadOnlyList<MailFolder> folders, IMailStore store)
    {
        foreach (var folder in folders)
        {
            var dir = new DirNode(parent.Unique(MailNames.Folder(folder.Name), isFile: false), folder, folder.Created, folder.Modified);
            parent.Add(dir);
            AddItems(dir, store.Items(folder));
            AddFolders(dir, folder.Folders, store);
        }
    }

    private static void AddItems(DirNode dir, IEnumerable<MailItemSummary> items)
    {
        foreach (var item in items)
        {
            if (item.Kind == MailItemKind.Contact)
            {
                dir.Add(new ContactNode(dir.Unique(MailNames.ContactFile(item.Subject), isFile: true), item));
                continue;
            }

            var (year, month) = MailNames.Period(item.Date);
            var yearDir = dir.GetOrAddPeriod(year);
            var monthDir = month is null ? yearDir : yearDir.GetOrAddPeriod(month);
            var name = item.Kind switch
            {
                MailItemKind.Appointment => MailNames.AppointmentFolder(item.Date, item.Subject),
                MailItemKind.Message => MailNames.MessageFolder(item.Date, item.Sender, item.Subject),
                _ => MailNames.ItemFolder(item.Date, item.Subject),
            };
            monthDir.Add(new ItemNode(monthDir.Unique(name, isFile: false), item));
            yearDir.Touch(item.Date);
            monthDir.Touch(item.Date);
        }
    }

    private sealed class Counts
    {
        public long Folders;
        public long Items;
        public long Files;
    }
}
