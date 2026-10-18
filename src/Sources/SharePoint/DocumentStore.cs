using AnyFS.Sdk;

namespace AnyFS.Sources.SharePoint;

/// <summary>A document library, or whatever a flavour serves as a top-level folder (a drive, a list, a container).</summary>
/// <param name="Key">What the store needs to address it: a Graph drive id, a server-relative URL, a container id.</param>
/// <param name="Name">The folder name it shows as.</param>
internal sealed record LibraryInfo(string Key, string Name, long? QuotaTotal, long? QuotaRemaining);

/// <summary>
/// What a listing knows about an item beyond its <see cref="VfsEntry"/>: the ids the next calls need. The
/// source treats it as opaque, except that <see cref="Id"/> and <see cref="Version"/> key the spool cache.
/// </summary>
/// <param name="Id">A stable id (Graph item id, SharePoint UniqueId).</param>
/// <param name="Version">Changes when the content changes (cTag, ETag).</param>
/// <param name="DownloadUrl">A pre-authenticated URL for the content, when the flavour has one.</param>
internal sealed record ItemRef(string Id, string? Version, string? DownloadUrl, long Size, bool IsFolder);

/// <summary>
/// The operations <see cref="SharePointSource"/> needs from a flavour, on paths inside a library. Every
/// method maps failures to <see cref="VfsException"/> and may throw <see cref="SignInRequiredException"/>
/// when the sign-in has lapsed. Item paths are '/'-separated, relative to the library, "" for its root.
/// </summary>
internal interface IDocumentStore : IDisposable
{
    /// <summary>What the source is serving, for log lines: the site's name or URL.</summary>
    string Description { get; }

    /// <summary>What a top-level folder is called in messages: "document library", or "container".</summary>
    string LibraryNoun { get; }

    Task<IReadOnlyList<LibraryInfo>> ListLibrariesAsync(CancellationToken ct);

    /// <summary>The children of a folder; throws NotFound for a missing folder. (The source checks for a file first.)</summary>
    IAsyncEnumerable<(VfsEntry Entry, ItemRef Item)> ListChildrenAsync(LibraryInfo library, string itemPath, CancellationToken ct);

    /// <summary>The item at a path, or null when there is none.</summary>
    Task<ItemRef?> GetItemAsync(LibraryInfo library, string itemPath, CancellationToken ct);

    /// <summary>
    /// The file's content from <paramref name="offset"/>, headers read and the body left to stream: 200 or 206,
    /// or 416 past the end. The store deals with expired download URLs itself.
    /// </summary>
    Task<HttpResponseMessage> OpenContentAsync(LibraryInfo library, string itemPath, ItemRef item, long offset, CancellationToken ct);

    /// <summary>Creates or replaces the file from a local file; the item as it is afterwards.</summary>
    Task<ItemRef> UploadAsync(LibraryInfo library, string itemPath, string localPath, long length, CancellationToken ct);

    /// <summary>Creates a folder; AlreadyExists when there is one; NotFound when the parent is missing.</summary>
    Task<ItemRef> CreateFolderAsync(LibraryInfo library, string itemPath, CancellationToken ct);

    Task DeleteAsync(LibraryInfo library, string itemPath, bool isFolder, CancellationToken ct);

    /// <summary>Renames or moves within the library; the target must not exist.</summary>
    Task<ItemRef> MoveAsync(LibraryInfo library, string fromPath, string toPath, bool isFolder, CancellationToken ct);

    Task SetTimesAsync(LibraryInfo library, string itemPath, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct);
}
