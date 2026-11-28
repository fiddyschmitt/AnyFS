using AnyFS.Sdk;
using AnyFS.Sources.Outlook.Mail;

namespace AnyFS.Sources.Outlook;

/// <summary>A node of the tree an Outlook source serves: a folder, an item's folder, or a contact's file.</summary>
internal abstract class TreeNode(string name)
{
    public string Name { get; } = name;

    /// <summary>The folder the node is in (set as it is added); null for the root.</summary>
    public DirNode? Parent { get; private set; }

    /// <summary>Where the node is in the tree, as the source's paths have it ("" for the root).</summary>
    public string PathInTree => Parent is null ? "" : VfsPath.Join(Parent.PathInTree, Name);

    public abstract VfsEntry Entry { get; }

    /// <summary>The item an item's folder or a contact's file is made of; null for a folder.</summary>
    public virtual MailItemSummary? Item => null;

    public void PlaceIn(DirNode parent) => Parent = parent;
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

    /// <summary>Why the folder's items could not be read, where they could not: the folder shows, without them, and says so when it has nothing else to show.</summary>
    public Exception? Failure { get; set; }

    public override VfsEntry Entry => VfsEntry.Directory(Name, Modified, Created);

    public TreeNode? Child(string name) => _byName.TryGetValue(name, out var node) ? node : null;

    public bool Has(string name) => _byName.ContainsKey(name);

    public void Add(TreeNode node)
    {
        _children.Add(node);
        _byName[node.Name] = node;
        node.PlaceIn(this);
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
    public override MailItemSummary Item => item;

    public override VfsEntry Entry => VfsEntry.Directory(Name, item.Date ?? item.Modified, item.Created ?? item.Date);
}

/// <summary>A contact: one .vcf file, made when it is listed or opened.</summary>
internal sealed class ContactNode(string name, MailItemSummary item) : TreeNode(name)
{
    public override MailItemSummary Item => item;

    /// <summary>The entry before the card is made: its size is a placeholder until then (see <see cref="OutlookSource"/>).</summary>
    public override VfsEntry Entry => VfsEntry.File(Name, 0, item.Modified ?? item.Date, item.Created);
}

/// <summary>
/// The tree an Outlook source serves (spec 7.7): the mailbox's folders as they are; under each, its items by year and
/// month, a folder per message named by date, sender and subject; contacts as .vcf files in their folder; the address
/// books beside them where the mailbox reaches its organisation's (<see cref="AddressBooks"/>). Immutable:
/// a refresh builds a new tree and the source swaps it in whole. Items are named from their listing cells alone; the
/// store opens none while the tree is built.
/// </summary>
internal sealed class MailTree
{
    private MailTree(DirNode root, long folders, long items, long files, IReadOnlyDictionary<string, TreeNode> byId)
    {
        Root = root;
        FolderCount = folders;
        ItemCount = items;
        FileCount = files;
        ById = byId;
    }

    public static MailTree Empty { get; } = new(new DirNode("", null, null, null), 0, 0, 0, new Dictionary<string, TreeNode>());

    /// <summary>The entry beside the folders of a folder whose own items could not be read: opened, it says why.</summary>
    public const string NotRead = "(could not be read)";

    public DirNode Root { get; }

    /// <summary>Each item's node (its folder, or a contact's file), by the item's id.</summary>
    public IReadOnlyDictionary<string, TreeNode> ById { get; }

    /// <summary>The folders, year and month folders and item folders.</summary>
    public long FolderCount { get; }

    public long ItemCount { get; }

    /// <summary>The files the items make (<see cref="ItemFiles.CountOf"/>: two per message, two per event, one per contact), attachments not counted.</summary>
    public long FileCount { get; }

    /// <param name="progress">Told of each folder as it is listed (how many are done, how many there are, the folder's name); null for nobody.</param>
    /// <param name="addressBooks">The address books' folder, put at the root first (a mailbox folder of its name is numbered); null for none.</param>
    /// <param name="showEmpty">Whether folders with nothing in them are shown (with Outlook's hidden ones, when asked for); else left out.</param>
    /// <param name="foldersOwn">
    /// Whether a failure to read a folder's items is that folder's own (refused to the account, gone since the folders
    /// were read) and not the store's as a whole (the network, the sign-in). Such a folder is shown without its items
    /// (<see cref="DirNode.Failure"/>) and the others are read as ever, unless none could be read at all, which is the
    /// mailbox's failure. Null takes every failure for the mailbox's, as all were: one folder the server refused meant
    /// no start, and at a refresh no new mail in any folder (the review of 21-Nov-2026, R12).
    /// </param>
    public static async Task<MailTree> BuildAsync(IMailStore store, CancellationToken ct, Action<int, int, string>? progress, AddressBooksNode? addressBooks = null, bool showEmpty = false, Func<Exception, bool>? foldersOwn = null)
    {
        var root = new DirNode("", null, null, null);
        if (addressBooks is not null)
        {
            root.Add(addressBooks);
        }

        var total = CountFolders(store.Folders);
        var done = 0;
        var read = new Reading(foldersOwn);
        await AddFoldersAsync(root, store.Folders, store, ct, name => progress?.Invoke(++done, total, name), showEmpty, read).ConfigureAwait(false);
        if (read.Folders == 0 && read.FirstFailure is { } nothingRead)
        {
            // not one folder could be read: the mailbox's failure, told as the first of them
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(nothingRead).Throw();
        }

        var counts = new Counts();
        Count(root, counts);
        return new MailTree(root, counts.Folders, counts.Items, counts.Files, counts.ById);
    }

    private static int CountFolders(IReadOnlyList<MailFolder> folders) => folders.Sum(f => 1 + CountFolders(f.Folders));

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
                    counts.Files += ItemFiles.CountOf(item.Item);
                    counts.ById.TryAdd(item.Item.Id, item);
                    break;
                case ContactNode contact:
                    counts.Items++;
                    counts.Files++;
                    counts.ById.TryAdd(contact.Item.Id, contact);
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

    /// <summary>
    /// The folders under <paramref name="parent"/>, each with its items and its own folders. A folder left with nothing in
    /// it (no item, and no folder with one: Journal, RSS Feeds, an empty Drafts) is not shown (the user, 26-Oct-2026: "Folders
    /// such as 'Journal' that are empty, let's not show them"), unless <paramref name="showEmpty"/>; a refresh that finds
    /// something in it shows it.
    /// </summary>
    private static async Task AddFoldersAsync(DirNode parent, IReadOnlyList<MailFolder> folders, IMailStore store, CancellationToken ct, Action<string> listed, bool showEmpty, Reading read)
    {
        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            var dir = new DirNode(parent.Unique(MailNames.Folder(folder.Name), isFile: false), folder, folder.Created, folder.Modified);
            try
            {
                AddItems(dir, await store.ItemsAsync(folder, ct).ConfigureAwait(false));
                read.Folders++;
            }
            catch (Exception ex) when (read.IsFoldersOwn(ex))
            {
                dir.Failure = ex;
                read.FirstFailure ??= ex;
            }

            listed(folder.Name);
            await AddFoldersAsync(dir, folder.Folders, store, ct, listed, showEmpty, read).ConfigureAwait(false);

            // One that could not be read and has folders of its own shows them, and beside them an entry that says why,
            // when it is opened, its own items are not there: shown its folders alone, it seemed to hold nothing of its
            // own and said nothing (the review of 21-Nov-2026, R12)
            if (dir.Failure is { } refused && dir.Children.Count > 0)
            {
                dir.Add(new DirNode(dir.Unique(NotRead, isFile: false), null, null, null) { Failure = refused });
            }

            // a folder that could not be read shows though it has nothing in it: that is where it says so
            if (showEmpty || dir.Children.Count > 0 || dir.Failure is not null)
            {
                parent.Add(dir);
            }
        }
    }

    /// <summary>How the reading of a mailbox's folders goes: how many were read, and the first that could not be.</summary>
    private sealed class Reading(Func<Exception, bool>? foldersOwn)
    {
        public int Folders { get; set; }

        public Exception? FirstFailure { get; set; }

        public bool IsFoldersOwn(Exception failure) => foldersOwn?.Invoke(failure) == true;
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
            // an event by its start; a message, and a meeting request or answer in a mail folder, by when it came and whom from
            var name = item.IsEvent
                ? MailNames.AppointmentFolder(item.Date, item.Subject)
                : item.Kind is MailItemKind.Message or MailItemKind.Appointment
                    ? MailNames.MessageFolder(item.Date, item.Sender, item.Subject)
                    : MailNames.ItemFolder(item.Date, item.Subject);
            monthDir.Add(new ItemNode(monthDir.Unique(name, isFile: false), item));
            yearDir.Touch(item.Date);
            monthDir.Touch(item.Date);
        }
    }

    private sealed class Counts
    {
        public readonly Dictionary<string, TreeNode> ById = new(StringComparer.Ordinal);
        public long Folders;
        public long Items;
        public long Files;
    }
}
