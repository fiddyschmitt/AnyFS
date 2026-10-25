using System.Collections.Concurrent;
using System.Diagnostics;
using AnyFS.Sdk;
using AnyFS.Sources.Outlook.Ews;
using AnyFS.Sources.Outlook.Files;
using AnyFS.Sources.Outlook.Mail;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Outlook;

/// <summary>The "Address Books" folder at the tree's root: what is under it comes from <see cref="AddressBooks"/>, not from the store.</summary>
internal sealed class AddressBooksNode(AddressBooks books) : TreeNode(MailNames.AddressBooksFolder)
{
    public AddressBooks Books => books;

    public override VfsEntry Entry => VfsEntry.Directory(Name, books.Changed, books.Changed);
}

/// <summary>An address list's folder: its name there and the list.</summary>
internal sealed record AddressBookFolder(string Name, AddressList List);

/// <summary>An address list's entries as files: each one's name (numbered where two share one), its card's length, its date.</summary>
internal sealed class AddressListView
{
    private readonly Dictionary<string, int> _byName;
    private readonly string[] _names;
    private readonly long[] _lengths;

    public AddressListView(AddressList list, DateTimeOffset taken, IReadOnlyList<DirectoryEntry> entries)
    {
        List = list;
        Taken = taken;
        Entries = entries;
        _names = new string[entries.Count];
        _lengths = new long[entries.Count];
        _byName = new Dictionary<string, int>(entries.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Count; i++)
        {
            var name = MailNames.ContactFile(entries[i].Name);
            var numbered = name;
            for (var n = 2; _byName.ContainsKey(numbered); n++)
            {
                numbered = MailNames.NumberedFile(name, n);
            }

            _byName[numbered] = i;
            _names[i] = numbered;
            _lengths[i] = ContactFile.Build(entries[i]).Length;
        }
    }

    public AddressList List { get; }

    /// <summary>When the entries were read.</summary>
    public DateTimeOffset Taken { get; }

    public IReadOnlyList<DirectoryEntry> Entries { get; }

    public int Count => Entries.Count;

    public VfsEntry EntryOf(int index) => VfsEntry.File(_names[index], _lengths[index], Entries[index].Changed, Entries[index].Changed);

    /// <summary>The entry a file name names, or null.</summary>
    public int? IndexOf(string name) => _byName.TryGetValue(name, out var index) ? index : null;
}

/// <summary>
/// A mailbox's organisation's address lists (spec 7.7, address books), served as "Address Books\(list)\(name).vcf" beside
/// the mailbox's folders: the lists asked for at the start, a list's entries when its folder is first listed (every
/// page), each entry's vCard made from what the list says of it, with no request per entry (a list may hold a whole
/// organisation). Kept in the source's state folder for the next start, and asked for again in the background once a day
/// old (an empty list after ten minutes: a directory not yet warm may answer none). The folder shows only when the
/// server gives lists.
/// </summary>
internal sealed class AddressBooks
{
    /// <summary>Entries asked for at a time.</summary>
    public const int PageSize = 500;

    /// <summary>How long what was read serves before it is asked for again (in the background).</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(1);

    private static readonly TimeSpan EmptyKeptFor = TimeSpan.FromMinutes(10);

    /// <summary>Where a reading stops, whatever the server says: more than any organisation's directory holds.</summary>
    private const int MostEntries = 2_000_000;

    private readonly IAddressBookService _service;
    private readonly string _mailbox;
    private readonly string _statePath;
    private readonly ILogger _logger;
    private readonly TimeSpan _coldPause;
    private readonly CancellationToken _stopping;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _saving = new(1, 1);
    private readonly Dictionary<string, AddressListMembers> _kept = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AddressListView> _views = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<AddressListView>>> _reading = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task>> _renewing = new(StringComparer.Ordinal);
    private IReadOnlyList<AddressBookFolder> _folders = [];
    private DateTimeOffset _listsTaken;
    private DateTimeOffset _listsChanged;
    private bool _listsKnown;

    /// <param name="mailbox">Whose address books: what the kept file is tagged with.</param>
    /// <param name="coldPause">How long to wait before asking a list that answered nothing once more.</param>
    /// <param name="stopping">Stops the readings in the background (the source's).</param>
    public AddressBooks(IAddressBookService service, string mailbox, string statePath, ILogger logger, TimeSpan coldPause, CancellationToken stopping)
    {
        _service = service;
        _mailbox = mailbox;
        _statePath = statePath;
        _logger = logger;
        _coldPause = coldPause;
        _stopping = stopping;
        Node = new AddressBooksNode(this);
    }

    public AddressBooksNode Node { get; }

    /// <summary>Whether the server gave address lists: the folder is shown only then.</summary>
    public bool HasLists => _folders.Count > 0;

    public IReadOnlyList<AddressBookFolder> Folders => _folders;

    /// <summary>When the lists last changed: the folders' date.</summary>
    public DateTimeOffset? Changed => _listsKnown ? _listsChanged : null;

    /// <summary>The entries known, of the lists read and kept from the last start: for the source's count of files.</summary>
    public long FileCount
    {
        get
        {
            lock (_gate)
            {
                return _views.Values.Sum(v => (long)v.Count) + _kept.Values.Sum(m => (long)m.Entries.Count);
            }
        }
    }

    public long FolderCount => HasLists ? 1 + _folders.Count : 0;

    /// <summary>Told when what the folder holds changes (lists or a list read), for the source's counts.</summary>
    public event Action? Updated;

    /// <summary>The list folder of this name, or null.</summary>
    public AddressBookFolder? Folder(string name) => _folders.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // --- the lists -----------------------------------------------------------------------------------------------------

    /// <summary>Takes in what was kept at the last start, or else asks for the lists (one request; a failure is said, and the folder left out until a refresh finds them).</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var kept = await Task.Run(() => AddressBookIndexFile.Load(_statePath, _mailbox), ct).ConfigureAwait(false);
        if (kept is null)
        {
            await ReadListsAsync(ct).ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            foreach (var members in kept.Members)
            {
                _kept[members.ListId] = members;
            }
        }

        SetLists(kept.Lists, kept.ListsTaken, kept.ListsChanged);
        if (DateTimeOffset.UtcNow - kept.ListsTaken >= KeptFor)
        {
            // a day old: served as kept, and asked for again behind
            _ = Task.Run(async () =>
            {
                try
                {
                    await ReadListsAsync(_stopping).ConfigureAwait(false);
                }
                catch (Exception ex) when (!_stopping.IsCancellationRequested)
                {
                    _logger.LogWarning("outlook: the address lists of {Mailbox} could not be read again; the last ones are still served: {Error}", _mailbox, ex.Message);
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>Asks for the lists again when they are a day old, or were never had (the source's refresh).</summary>
    public Task RefreshAsync(CancellationToken ct) => _listsKnown && DateTimeOffset.UtcNow - _listsTaken < KeptFor ? Task.CompletedTask : ReadListsAsync(ct);

    private async Task ReadListsAsync(CancellationToken ct)
    {
        try
        {
            var lists = await _service.AddressListsAsync(ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            SetLists(lists, now, now);
            _logger.LogInformation("outlook: {Count:N0} address lists for {Mailbox}", lists.Count, _mailbox);
            await SaveAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EwsException or HttpRequestException or SignInRequiredException or TimeoutException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            if (_listsKnown)
            {
                _logger.LogWarning("outlook: the address lists of {Mailbox} could not be read again; the last ones are still served: {Error}", _mailbox, ex.Message);
            }
            else
            {
                _logger.LogWarning("outlook: the address lists of {Mailbox} could not be read; the Address Books folder is left out until they can be: {Error}", _mailbox, ex.Message);
            }
        }
    }

    private void SetLists(IReadOnlyList<AddressList> lists, DateTimeOffset taken, DateTimeOffset changed)
    {
        var folders = new List<AddressBookFolder>(lists.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in lists)
        {
            var name = MailNames.Folder(list.Name);
            var numbered = name;
            for (var n = 2; !used.Add(numbered); n++)
            {
                numbered = MailNames.NumberedFolder(name, n);
            }

            folders.Add(new AddressBookFolder(numbered, list));
        }

        lock (_gate)
        {
            if (!_listsKnown || !_folders.Select(f => f.List).SequenceEqual(lists))
            {
                _listsChanged = changed;
            }

            _folders = folders;
            _listsTaken = taken;
            _listsKnown = true;
            var ids = lists.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in _views.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                _views.TryRemove(gone, out _);
            }

            foreach (var gone in _kept.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                _kept.Remove(gone);
            }
        }

        Updated?.Invoke();
    }

    // --- a list's entries ----------------------------------------------------------------------------------------------

    /// <summary>A list's entries as files: read whole at the first ask (or taken from what was kept), then served as read, and read again in the background once old.</summary>
    public async Task<AddressListView> MembersAsync(AddressList list, CancellationToken ct)
    {
        if (_views.TryGetValue(list.Id, out var view))
        {
            RenewIfOld(view);
            return view;
        }

        AddressListMembers? kept;
        lock (_gate)
        {
            _kept.Remove(list.Id, out kept);
        }

        if (kept is not null)
        {
            view = _views.GetOrAdd(list.Id, _ => new AddressListView(list, kept.Taken, kept.Entries));
            RenewIfOld(view);
            return view;
        }

        // the first reading: who asks meanwhile waits for the same one; one that failed is tried again at the next ask
        var reading = _reading.GetOrAdd(list.Id, _ => new Lazy<Task<AddressListView>>(() => _views.TryGetValue(list.Id, out var read) ? Task.FromResult(read) : ReadAsync(list, null, _stopping)));
        try
        {
            return await reading.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (reading.Value.IsCompleted)
            {
                _reading.TryRemove(new KeyValuePair<string, Lazy<Task<AddressListView>>>(list.Id, reading));
            }
        }
    }

    /// <summary>Reads a list's entries whole, dates them (an entry as it was keeps its date), and serves and keeps them.</summary>
    private async Task<AddressListView> ReadAsync(AddressList list, AddressListView? previous, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var entries = new List<DirectoryEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        var cold = true;
        while (offset < MostEntries)
        {
            var page = await _service.AddressListPageAsync(list.Id, offset, PageSize, ct).ConfigureAwait(false);
            if (page.Entries.Count == 0)
            {
                if (offset == 0 && cold)
                {
                    // a directory not yet warm may answer nothing at first (Spike 11): once more, after a moment
                    cold = false;
                    await Task.Delay(_coldPause, ct).ConfigureAwait(false);
                    continue;
                }

                break;
            }

            // a list that changes while it is read may give an entry twice
            offset += page.Entries.Count;
            entries.AddRange(page.Entries.Where(e => seen.Add(e.Id)));
            if (page.Total > 0 ? offset >= page.Total : page.Entries.Count < PageSize)
            {
                break;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var view = new AddressListView(list, now, Dated(entries, previous?.Entries, now));
        _views[list.Id] = view;
        _logger.LogInformation("outlook: the address list {List} of {Mailbox}: {Count:N0} entries, read in {Seconds:F1} s", list.Name, _mailbox, view.Count, Stopwatch.GetElapsedTime(started).TotalSeconds);
        Updated?.Invoke();
        await SaveAsync(ct).ConfigureAwait(false);
        return view;
    }

    /// <summary>The entries dated: one as it was at the last reading keeps its date; a new or changed one is dated now.</summary>
    private static List<DirectoryEntry> Dated(List<DirectoryEntry> entries, IReadOnlyList<DirectoryEntry>? previous, DateTimeOffset now)
    {
        var before = new Dictionary<string, DirectoryEntry>(StringComparer.Ordinal);
        foreach (var e in previous ?? [])
        {
            before.TryAdd(e.Id, e);
        }

        return entries.Select(e => before.TryGetValue(e.Id, out var old) && old.Changed is { } date && Same(old, e) ? e with { Changed = date } : e with { Changed = now }).ToList();
    }

    private static bool Same(DirectoryEntry a, DirectoryEntry b) => ContactFile.Build(a with { Changed = null }).AsSpan().SequenceEqual(ContactFile.Build(b with { Changed = null }));

    private void RenewIfOld(AddressListView view)
    {
        if (DateTimeOffset.UtcNow - view.Taken < (view.Count == 0 ? EmptyKeptFor : KeptFor))
        {
            return;
        }

        var renewal = new Lazy<Task>(() => Task.Run(async () =>
        {
            try
            {
                await ReadAsync(view.List, view, _stopping).ConfigureAwait(false);
            }
            catch (Exception ex) when (!_stopping.IsCancellationRequested)
            {
                _logger.LogWarning("outlook: the address list {List} of {Mailbox} could not be read again; the last reading is still served: {Error}", view.List.Name, _mailbox, ex.Message);
            }
            finally
            {
                _renewing.TryRemove(view.List.Id, out _);
            }
        }));
        if (_renewing.TryAdd(view.List.Id, renewal))
        {
            _ = renewal.Value;
        }
    }

    /// <summary>Keeps the lists, and the entries of every list read, for the next start; a file that cannot be written is said and left.</summary>
    private async Task SaveAsync(CancellationToken ct)
    {
        AddressBookIndex index;
        lock (_gate)
        {
            var members = _views.Values.Select(v => new AddressListMembers(v.List.Id, v.Taken, v.Entries)).Concat(_kept.Values).ToList();
            index = new AddressBookIndex(AddressBookIndexFile.Version, _mailbox, _listsTaken, _listsChanged, _folders.Select(f => f.List).ToList(), members);
        }

        await _saving.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => AddressBookIndexFile.Save(_statePath, index), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("outlook: the address books of {Mailbox} could not be kept for the next start: {Error}", _mailbox, ex.Message);
        }
        finally
        {
            _saving.Release();
        }
    }

    // --- what the folder serves ----------------------------------------------------------------------------------------

    /// <summary>The entry a path under the folder names (the folder, a list's folder, an entry's card), or null.</summary>
    public async Task<VfsEntry?> StatAsync(IReadOnlyList<string> rest, CancellationToken ct)
    {
        if (rest.Count == 0)
        {
            return Node.Entry;
        }

        if (Folder(rest[0]) is not { } folder)
        {
            return null;
        }

        if (rest.Count == 1)
        {
            return FolderEntry(folder);
        }

        // a name no card has (desktop.ini, say) is not looked for: that would read the whole list
        if (rest.Count > 2 || !rest[1].EndsWith(".vcf", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var view = await MembersAsync(folder.List, ct).ConfigureAwait(false);
        return view.IndexOf(rest[1]) is { } index ? view.EntryOf(index) : null;
    }

    /// <summary>What a folder under it holds (the lists, or a list's cards), or null when the path names no folder here.</summary>
    public async Task<IReadOnlyList<VfsEntry>?> ListAsync(IReadOnlyList<string> rest, CancellationToken ct)
    {
        if (rest.Count == 0)
        {
            return _folders.Select(FolderEntry).ToList();
        }

        if (rest.Count != 1 || Folder(rest[0]) is not { } folder)
        {
            return null;
        }

        var view = await MembersAsync(folder.List, ct).ConfigureAwait(false);
        var entries = new VfsEntry[view.Count];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = view.EntryOf(i);
        }

        return entries;
    }

    /// <summary>The card a path names, or null.</summary>
    public async Task<byte[]?> CardAsync(IReadOnlyList<string> rest, CancellationToken ct)
    {
        if (rest.Count != 2 || Folder(rest[0]) is not { } folder || !rest[1].EndsWith(".vcf", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var view = await MembersAsync(folder.List, ct).ConfigureAwait(false);
        return view.IndexOf(rest[1]) is { } index ? ContactFile.Build(view.Entries[index]) : null;
    }

    private VfsEntry FolderEntry(AddressBookFolder folder) => VfsEntry.Directory(folder.Name, Changed, Changed);
}
