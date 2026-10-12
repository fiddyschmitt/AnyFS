namespace AnyFS.Sdk;

[Flags]
public enum VfsAttributes
{
    None = 0,
    Hidden = 1,
    System = 2,
    /// <summary>The upstream marks the item read-only (a file attribute, not the AnyFS read-only layers).</summary>
    ReadOnly = 4,
}

/// <summary>One directory entry or stat result.</summary>
/// <param name="Name">The entry's own name (no separators). The root's name is empty.</param>
/// <param name="ETag">An optional version tag from the upstream; HTTP-style providers use it when present.</param>
public sealed record VfsEntry(
    string Name,
    bool IsDirectory,
    long Length,
    DateTimeOffset? Created,
    DateTimeOffset? Modified,
    VfsAttributes Attributes = VfsAttributes.None,
    string? ETag = null)
{
    public static VfsEntry Directory(string name, DateTimeOffset? modified = null, DateTimeOffset? created = null, VfsAttributes attributes = VfsAttributes.None)
        => new(name, true, 0, created, modified, attributes);

    public static VfsEntry File(string name, long length, DateTimeOffset? modified = null, DateTimeOffset? created = null, VfsAttributes attributes = VfsAttributes.None, string? etag = null)
        => new(name, false, length, created, modified, attributes, etag);
}

/// <summary>Free/total space, for providers that report quota (statfs, WebDAV quota).</summary>
public sealed record VfsSpace(long TotalBytes, long FreeBytes);
