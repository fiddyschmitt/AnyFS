using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using AnyFS.Sdk;
using AnyFS.Sources.Common;
using AnyFS.Sources.Outlook.Mail;

namespace AnyFS.Sources.Outlook;

/// <summary>A file an item's folder lists: its name and length.</summary>
internal sealed record ListedFile(string Name, long Length);

/// <summary>
/// What an item's folder lists, without the item: its files' names and lengths, its attachments folder's, and the dates
/// they show (or, for a contact, its card's).
/// </summary>
internal sealed record ItemListing(IReadOnlyList<ListedFile> Files, IReadOnlyList<ListedFile> Attachments, DateTimeOffset? Modified, DateTimeOffset? Created)
{
    public bool HasAttachmentsFolder => Attachments.Count > 0;

    /// <summary>The files it holds, its attachments' with them.</summary>
    public int FileCount => Files.Count + Attachments.Count;

    public ListedFile? File(string name) => Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public ListedFile? Attachment(string name) => Attachments.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public VfsEntry EntryOf(ListedFile file) => VfsEntry.File(file.Name, file.Length, Modified, Created);

    public VfsEntry AttachmentsEntry => VfsEntry.Directory(MailNames.AttachmentsFolder, Modified, Created);

    public static ItemListing Of(ItemFiles files)
        => new([.. files.Files.Select(f => new ListedFile(f.Name, f.Length))], [.. files.Attachments.Select(f => new ListedFile(f.Name, f.Length))], files.Modified, files.Created);

    /// <summary>Whether it lists the same files with the same lengths and dates (records of lists compare the lists themselves, not what they hold).</summary>
    public bool SameAs(ItemListing other) => Files.SequenceEqual(other.Files) && Attachments.SequenceEqual(other.Attachments) && Modified == other.Modified && Created == other.Created;
}

/// <summary>
/// Each item's listing as it was last made (spec 7.7: names, lengths and dates, never content), by the item's id, with a
/// stamp of the item as its folder listed it then: when it last changed, its size, its date and its kind. While the
/// item's folder lists it with the same stamp, its listing serves without the store being asked for the item, so a walk
/// through folders already seen asks nothing, and from one start to the next too: kept in the source's state folder
/// with the local time zone (the Markdown gives local times). Put right whenever the item is opened and its files come
/// out otherwise. An S/MIME message's is not kept: what it shows depends on the certificates at hand.
/// </summary>
internal sealed class ItemListings
{
    /// <summary>
    /// Bumped when what an item's files hold changes (MessageMarkdown, MimeWriter, CalendarFile, ContactFile,
    /// AttachmentNames, ItemFiles): lengths kept before would no longer be the files'. An older file is not read.
    /// </summary>
    public const int Version = 1;

    public const string FileName = "item-listings.json.gz";

    private readonly ConcurrentDictionary<string, (string Stamp, ItemListing Listing)> _kept = new(StringComparer.Ordinal);
    private long _changes;

    public int Count => _kept.Count;

    /// <summary>Counts the changes to what is kept, so it is saved again only when it changed.</summary>
    public long Changes => Interlocked.Read(ref _changes);

    /// <summary>The item as its folder lists it, in what its files depend on.</summary>
    public static string StampOf(MailItemSummary item)
        => string.Create(CultureInfo.InvariantCulture, $"{item.Modified?.UtcTicks}|{item.Size}|{item.Date?.UtcTicks}|{(int)item.Kind}|{(item.IsEvent ? 1 : 0)}|{(item.HasAttachments ? 1 : 0)}");

    /// <summary>The listing kept for the item as its folder lists it now; null when there is none, or it was made from the item as it was before.</summary>
    public ItemListing? For(MailItemSummary item) => _kept.TryGetValue(item.Id, out var kept) && kept.Stamp == StampOf(item) ? kept.Listing : null;

    /// <summary>Keeps an item's listing as made from it now; true when that changed what was kept (none before, the item changed since, or other lengths).</summary>
    public bool Keep(MailItemSummary item, ItemListing listing)
    {
        var stamp = StampOf(item);
        while (true)
        {
            if (_kept.TryGetValue(item.Id, out var kept))
            {
                if (kept.Stamp == stamp && kept.Listing.SameAs(listing))
                {
                    return false;
                }

                if (!_kept.TryUpdate(item.Id, (stamp, listing), kept))
                {
                    continue;
                }
            }
            else if (!_kept.TryAdd(item.Id, (stamp, listing)))
            {
                continue;
            }

            Interlocked.Increment(ref _changes);
            return true;
        }
    }

    /// <summary>Takes in what was kept at <paramref name="path"/> for this store under this time zone; nothing when there is none, or it was kept otherwise.</summary>
    /// <param name="store">Whose items: the mailbox's name, the data file's path.</param>
    public void Load(string path, string store)
    {
        if (StateFile.Load(path, ItemListingsJson.Default.ItemListingIndex) is not { Version: Version } index || index.Store != store || index.Zone != TimeZoneInfo.Local.Id)
        {
            return;
        }

        foreach (var kept in index.Items)
        {
            _kept.TryAdd(kept.Id, (kept.Stamp, new ItemListing(kept.Files, kept.Attachments ?? [], kept.Modified, kept.Created)));
        }
    }

    /// <summary>Keeps the listings of the items <paramref name="current"/> says are still there at <paramref name="path"/>, the rest forgotten.</summary>
    /// <returns>How many were kept.</returns>
    public int Save(string path, string store, Func<string, bool> current)
    {
        foreach (var gone in _kept.Keys.Where(id => !current(id)).ToList())
        {
            _kept.TryRemove(gone, out _);
        }

        var items = _kept.Select(e => new KeptListing(e.Key, e.Value.Stamp, e.Value.Listing.Files, e.Value.Listing.HasAttachmentsFolder ? e.Value.Listing.Attachments : null, e.Value.Listing.Modified, e.Value.Listing.Created)).ToList();
        StateFile.Save(path, new ItemListingIndex(Version, store, TimeZoneInfo.Local.Id, items), ItemListingsJson.Default.ItemListingIndex);
        return items.Count;
    }
}

/// <summary>One item's listing as kept: the item's id and stamp, then the listing.</summary>
internal sealed record KeptListing(string Id, string Stamp, IReadOnlyList<ListedFile> Files, IReadOnlyList<ListedFile>? Attachments, DateTimeOffset? Modified, DateTimeOffset? Created);

/// <summary>The listings kept between starts: whose store, under which time zone, and each item's.</summary>
internal sealed record ItemListingIndex(int Version, string Store, string Zone, IReadOnlyList<KeptListing> Items);

/// <summary>The kept listings' JSON, generated at build time (no reflection, so trimming keeps it working).</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ItemListingIndex))]
internal sealed partial class ItemListingsJson : JsonSerializerContext;
