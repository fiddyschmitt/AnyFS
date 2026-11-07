using System.Collections.Concurrent;

namespace AnyFS.Sdk;

/// <summary>
/// A listing held by a <see cref="ListingCache"/>: when it arrived from the upstream, the folder's stamp as read before
/// it (<see cref="ListingCache.StampOf"/>), how many entries it has and how many of them are folders, and the entries: as
/// bytes (<see cref="ListingCodec"/>) in a cache that keeps its listings, as they are in a cache that holds them for a time.
/// </summary>
internal sealed class StoredListing
{
    /// <summary>When it arrived (the time provider's timestamp): it expires from then, where listings expire.</summary>
    public long Fetched;

    /// <summary>A listing held for a time, as it is.</summary>
    public StoredListing(long fetched, string? stamp, IReadOnlyList<VfsEntry> list)
    {
        Fetched = fetched;
        Stamp = stamp;
        List = list;
        Count = list.Count;
        var folders = 0;
        foreach (var entry in list)
        {
            if (entry.IsDirectory)
            {
                folders++;
            }
        }

        Folders = folders;
    }

    /// <summary>A listing kept, as bytes.</summary>
    public StoredListing(long fetched, string? stamp, byte[] packed, int count, int folders)
    {
        Fetched = fetched;
        Stamp = stamp;
        Packed = packed;
        Count = count;
        Folders = folders;
    }

    public string? Stamp { get; }

    public int Count { get; }

    public int Folders { get; }

    public int Files => Count - Folders;

    public byte[]? Packed { get; }

    public IReadOnlyList<VfsEntry>? List { get; }

    /// <summary>A listing kept, packed from its entries.</summary>
    public static StoredListing Pack(long fetched, string? stamp, IReadOnlyList<VfsEntry> list)
    {
        var packed = ListingCodec.Pack(list, out var folders);
        return new StoredListing(fetched, stamp, packed, list.Count, folders);
    }

    /// <summary>The same listing with another stamp.</summary>
    public StoredListing WithStamp(string? stamp) => Packed is not null ? new StoredListing(Fetched, stamp, Packed, Count, Folders) : new StoredListing(Fetched, stamp, List!);

    /// <summary>The entries: as held, or made from the bytes.</summary>
    public IReadOnlyList<VfsEntry> Entries() => List ?? ListingCodec.Unpack(Packed!, Count);

    /// <summary>The names of the folders in it, nothing else made.</summary>
    public List<string> FolderNames()
    {
        if (List is null)
        {
            return ListingCodec.FolderNames(Packed!, Count, Folders);
        }

        var names = new List<string>(Folders);
        foreach (var entry in List)
        {
            if (entry.IsDirectory)
            {
                names.Add(entry.Name);
            }
        }

        return names;
    }

    /// <summary>Whether it says what <paramref name="other"/> says: the same stamp and the same entries.</summary>
    public bool SameAs(StoredListing other)
    {
        if (Stamp != other.Stamp || Count != other.Count || Folders != other.Folders)
        {
            return false;
        }

        if (Packed is not null && other.Packed is not null)
        {
            return Packed.AsSpan().SequenceEqual(other.Packed);
        }

        if (List is not null && other.List is not null)
        {
            return List.SequenceEqual(other.List);
        }

        return Entries().SequenceEqual(other.Entries());
    }
}

/// <summary>
/// Where a <see cref="ListingCache"/> holds its listings, by folder path: in memory (<see cref="MemoryListingStore"/>), or on
/// disk for a source that keeps the listings of more than fits. Everything but
/// <see cref="Get"/> and <see cref="All"/> is called under the cache's gate.
/// </summary>
internal interface IListingStore : IDisposable
{
    /// <summary>How many listings are held.</summary>
    int Count { get; }

    /// <summary>What is held for <paramref name="path"/>; null where nothing is. Called without the gate.</summary>
    StoredListing? Get(string path);

    /// <summary>Holds <paramref name="listing"/> for <paramref name="path"/>; whether nothing was held for it before.</summary>
    bool Set(string path, StoredListing listing);

    /// <summary>Lets go of what is held for <paramref name="path"/>; false where nothing was.</summary>
    bool Remove(string path);

    void Clear();

    /// <summary>What is held at <paramref name="folder"/> and below it, the folder's own first, then the rest in path order; everything for the root.</summary>
    List<(string Path, StoredListing Listing)> Under(string folder);

    /// <summary>Every path held, in order.</summary>
    List<string> Paths();

    /// <summary>Every listing held, a moment's view, nothing copied. Called without the gate.</summary>
    IEnumerable<(string Path, StoredListing Listing)> All();
}

/// <summary>The listings in memory, with their paths in order, so the folders below one are found by a range of them, not by a pass over them all.</summary>
internal sealed class MemoryListingStore : IListingStore
{
    private readonly ConcurrentDictionary<string, StoredListing> _entries = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _keys = new(StringComparer.Ordinal);
    private int _count;

    // the sorted set counts what is held at no cost: the dictionary's own Count takes every one of its locks, at every
    // listing stored, which made bringing back a big run's listings take half a minute
    public int Count => Volatile.Read(ref _count);

    public StoredListing? Get(string path) => _entries.TryGetValue(path, out var held) ? held : null;

    public bool Set(string path, StoredListing listing)
    {
        _entries[path] = listing;
        if (!_keys.Add(path))
        {
            return false;
        }

        Interlocked.Increment(ref _count);
        return true;
    }

    public bool Remove(string path)
    {
        if (!_entries.TryRemove(path, out _))
        {
            return false;
        }

        _keys.Remove(path);
        Interlocked.Decrement(ref _count);
        return true;
    }

    public void Clear()
    {
        _entries.Clear();
        _keys.Clear();
        Volatile.Write(ref _count, 0);
    }

    public List<(string Path, StoredListing Listing)> Under(string folder)
    {
        var held = new List<(string, StoredListing)>();
        foreach (var path in KeysUnder(folder))
        {
            if (_entries.TryGetValue(path, out var listing))
            {
                held.Add((path, listing));
            }
        }

        return held;
    }

    public List<string> Paths() => [.. _keys];

    public IEnumerable<(string Path, StoredListing Listing)> All()
    {
        foreach (var (path, listing) in _entries)
        {
            yield return (path, listing);
        }
    }

    /// <summary>
    /// The paths held at <paramref name="folder"/> and below it, in order: the folder's own, then the range its children's
    /// paths fall in (in ordinal order every "folder/…" lies between "folder/" and "folder0", '0' being the character after
    /// '/'); every path for the root.
    /// </summary>
    private IEnumerable<string> KeysUnder(string folder)
    {
        if (folder.Length == 0)
        {
            return _keys;
        }

        var below = _keys.GetViewBetween(folder + "/", folder + "0").Where(path => path.Length > folder.Length && path[folder.Length] == '/');
        return _keys.Contains(folder) ? below.Prepend(folder) : below;
    }

    public void Dispose()
    {
    }
}
