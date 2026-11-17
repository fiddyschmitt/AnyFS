using System.Text.Json;
using AnyFS.Sdk;

namespace AnyFS.Sources.Artifactory;

/// <summary>A repository as Artifactory lists it (<c>api/repositories</c>).</summary>
/// <param name="Key">Its key, the name it goes by everywhere.</param>
/// <param name="Type"><c>LOCAL</c>, <c>REMOTE</c>, <c>VIRTUAL</c>, <c>FEDERATED</c>, or another kind that holds its own files.</param>
/// <param name="PackageType">What it holds (<c>Maven</c>, <c>Docker</c>, <c>Generic</c>…), as Artifactory writes it.</param>
internal sealed record ArtRepository(string Key, string Type, string PackageType, string? Description)
{
    public const string Remote = "REMOTE";
    public const string Virtual = "VIRTUAL";

    /// <summary>What a remote repository's files are kept under: Artifactory holds what it has fetched from the other server in <c>&lt;key&gt;-cache</c>.</summary>
    public const string CacheSuffix = "-cache";

    public bool IsRemote => Type.Equals(Remote, StringComparison.OrdinalIgnoreCase);

    public bool IsVirtual => Type.Equals(Virtual, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The key its files are listed and read under: a remote repository's cache, so nothing AnyFS asks makes Artifactory
    /// fetch a file from the other server; any other repository's own key.
    /// </summary>
    public string StorageKey => IsRemote ? Key + CacheSuffix : Key;

    public static ArtRepository Parse(JsonElement e)
        => new(e.Str("key") ?? "", (e.Str("type") ?? "LOCAL").ToUpperInvariant(), e.Str("packageType") ?? "", e.Str("description"));

    /// <summary>
    /// The repository's folder in a server's root, under <paramref name="name"/>: its key carries the repository's kind and
    /// key (<c>REMOTE:npm-remote</c>), so a path below it is found from the kept listing alone, nothing asked.
    /// </summary>
    public VfsEntry Entry(string name) => VfsEntry.Directory(name) with { Key = Type + ":" + Key };

    /// <summary>The repository a server root's entry is (<see cref="Entry"/>); one without such a key is taken for a local repository of its name.</summary>
    public static ArtRepository Of(VfsEntry entry)
    {
        var colon = entry.Key?.IndexOf(':', StringComparison.Ordinal) ?? -1;
        return colon > 0 ? new ArtRepository(entry.Key![(colon + 1)..], entry.Key[..colon], "", null) : new ArtRepository(entry.Name, "LOCAL", "", null);
    }
}

/// <summary>A file or folder in a repository, as a listing gives it.</summary>
/// <param name="Folder">Its folder's path in the repository; empty for the repository's root.</param>
/// <param name="Name">Its own name, as Artifactory writes it.</param>
/// <param name="Sha1">A file's SHA-1, in hex; null for a folder, or where the listing does not tell.</param>
internal sealed record ArtItem(string Folder, string Name, bool IsFolder, long Size, DateTimeOffset? Created, DateTimeOffset? Modified, string? Sha1)
{
    /// <summary>Its path in the repository.</summary>
    public string Path => Folder.Length == 0 ? Name : Folder + "/" + Name;

    /// <summary>An entry of the file list call's answer (<c>api/storage/…?list</c>): <c>uri</c> is its path below the folder asked for.</summary>
    public static ArtItem? ParseListed(JsonElement e, string folder)
    {
        var below = (e.Str("uri") ?? "").Trim('/');
        if (below.Length == 0)
        {
            return null;
        }

        var slash = below.LastIndexOf('/');
        var parent = slash < 0 ? folder : (folder.Length == 0 ? below[..slash] : folder + "/" + below[..slash]);
        var isFolder = e.Bool("folder");
        var modified = e.Date("lastModified");
        return new ArtItem(parent, below[(slash + 1)..], isFolder, isFolder ? 0 : Math.Max(0, e.Long("size")), modified, modified, isFolder ? null : Hex(e.Str("sha1")));
    }

    /// <summary>A checksum as an entry's tag where it is one: forty hex digits, in lower case; null otherwise.</summary>
    public static string? Hex(string? text)
        => text is { Length: 40 } && text.All(char.IsAsciiHexDigit) ? text.ToLowerInvariant() : null;
}

/// <summary>
/// What a folder's entries show as, and what each keeps to be found by (spec 7.13). A file or folder keeps its own name
/// wherever Windows takes it; one Windows refuses is changed as little as makes it one (the SDK's <see cref="SafeNames"/>),
/// and two that differ only in case, which Artifactory keeps apart and AnyFS's paths do not, are told apart with a number.
/// An entry shown under another name than its own carries its own as its key, which is what Artifactory is asked by.
/// </summary>
internal static class ArtifactoryNames
{
    /// <summary>The version of the keys: listings saved under another are not read back.</summary>
    public const int Format = 1;

    /// <summary>
    /// A folder's entries from its items: folders first, then files, each by name; the names made ones a client takes, each
    /// entry with its own name as its key where it shows under another.
    /// </summary>
    public static IReadOnlyList<VfsEntry> Entries(IEnumerable<ArtItem> items)
    {
        var ordered = items.OrderBy(i => i.IsFolder ? 0 : 1).ThenBy(i => i.Name, StringComparer.Ordinal).ToList();
        var names = SafeNames.Unique(ordered.Select(i => i.Name).ToList());
        var entries = new List<VfsEntry>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            var key = string.Equals(names[i], item.Name, StringComparison.Ordinal) ? null : item.Name;
            entries.Add(item.IsFolder
                ? VfsEntry.Directory(names[i], item.Modified, item.Created) with { Key = key }
                : VfsEntry.File(names[i], item.Size, item.Modified, item.Created ?? item.Modified, etag: item.Sha1) with { Key = key });
        }

        return entries;
    }

    /// <summary>What Artifactory calls an entry: its key where it has one, else the name it shows under.</summary>
    public static string Own(VfsEntry entry) => entry.Key ?? entry.Name;
}
