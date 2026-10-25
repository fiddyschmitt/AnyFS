using System.Text.Json.Serialization;

namespace AnyFS.Sources.Outlook.Mail;

/// <summary>An address list of the organisation's directory (the Global Address List, All Users, All Rooms...), as its server names it.</summary>
/// <param name="Id">The server's id for it (an address list's GUID), what its members are asked by.</param>
internal sealed record AddressList(string Id, string Name);

/// <summary>A telephone number of a directory entry, with its vCard type ("WORK,VOICE", "CELL,VOICE").</summary>
internal sealed record DirectoryPhone(string Type, string Number);

/// <summary>A postal address of a directory entry, with its vCard type ("WORK", "HOME", "POSTAL").</summary>
internal sealed record DirectoryAddress(string Type, string? Street, string? City, string? State, string? PostalCode, string? Country);

/// <summary>One entry of an address list (a person, a group, a room) as the directory lists it: what its vCard is made of.</summary>
/// <param name="Id">The server's id for it (a persona's), stable between listings.</param>
/// <param name="Name">Its display name: the file's name.</param>
/// <param name="Kind">"Person", "DistributionList", "Room"...; null when the server does not say.</param>
internal sealed record DirectoryEntry(string Id, string Name, string? Kind = null)
{
    public string? GivenName { get; init; }

    public string? MiddleName { get; init; }

    public string? Surname { get; init; }

    public string? Nickname { get; init; }

    public string? Company { get; init; }

    public string? Department { get; init; }

    public string? JobTitle { get; init; }

    /// <summary>The office ("Building 4, 2.10").</summary>
    public string? Office { get; init; }

    /// <summary>The SMTP addresses, the primary first.</summary>
    public IReadOnlyList<string> Emails { get; init; } = [];

    public IReadOnlyList<DirectoryPhone> Phones { get; init; } = [];

    public IReadOnlyList<DirectoryAddress> Addresses { get; init; } = [];

    public string? ImAddress { get; init; }

    /// <summary>When AnyFS first saw the entry as it is now: its file's date and its card's REV. Set by <see cref="AddressBooks"/>, not the server.</summary>
    public DateTimeOffset? Changed { get; init; }
}

/// <summary>A page of an address list's entries and how many the list has in all (0 when the server does not say).</summary>
internal sealed record DirectoryPage(IReadOnlyList<DirectoryEntry> Entries, int Total);

/// <summary>
/// What a mailbox's service offers of its organisation's directory (spec 7.7, address books): the address lists, and a
/// list's entries a page at a time. Outlook on the web answers both (GetPeopleFilters, FindPeople; Spike 11).
/// </summary>
internal interface IAddressBookService
{
    /// <summary>The organisation's address lists, in the order the server gives them; the mailbox's own contact folders left out (they are folders of the mailbox).</summary>
    Task<IReadOnlyList<AddressList>> AddressListsAsync(CancellationToken ct);

    /// <summary>A page of a list's entries, from <paramref name="offset"/>, in the server's order (by name).</summary>
    Task<DirectoryPage> AddressListPageAsync(string listId, int offset, int count, CancellationToken ct);
}

/// <summary>A list's entries as last read, and when.</summary>
internal sealed record AddressListMembers(string ListId, DateTimeOffset Taken, IReadOnlyList<DirectoryEntry> Entries);

/// <summary>
/// A mailbox's address books kept between starts in its source's state folder, beside its listing: the address lists
/// and the entries of each list that was opened, so a restart serves them at once and asks again only when they are a
/// day old. The directory's cards (names, addresses, telephone numbers) only; never a message.
/// </summary>
/// <param name="ListsTaken">When the address lists were last asked for.</param>
/// <param name="ListsChanged">When they were last seen to change: the folder's date.</param>
internal sealed record AddressBookIndex(int Version, string Mailbox, DateTimeOffset ListsTaken, DateTimeOffset ListsChanged, IReadOnlyList<AddressList> Lists, IReadOnlyList<AddressListMembers> Members);

internal static class AddressBookIndexFile
{
    /// <summary>Bumped when the shape changes; an older file is then not read.</summary>
    public const int Version = 1;

    public const string FileName = "address-books.json.gz";

    public static void Save(string path, AddressBookIndex index) => StateFile.Save(path, index, AddressBookJson.Default.AddressBookIndex);

    /// <summary>What is kept at <paramref name="path"/> for this mailbox, or null when there is nothing, it cannot be read, or it is another mailbox's.</summary>
    public static AddressBookIndex? Load(string path, string mailbox)
        => StateFile.Load(path, AddressBookJson.Default.AddressBookIndex) is { Version: Version } index && index.Mailbox == mailbox ? index : null;
}

/// <summary>The kept address books' JSON, generated at build time (no reflection, so trimming keeps it working).</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AddressBookIndex))]
internal sealed partial class AddressBookJson : JsonSerializerContext;
