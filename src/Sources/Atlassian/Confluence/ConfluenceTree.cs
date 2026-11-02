using AnyFS.Sdk;

namespace AnyFS.Sources.Atlassian.Confluence;

/// <summary>
/// One thing in a Confluence source's tree (spec 7.10): a folder (the server, a list of spaces, a space, a page, the
/// blog) or a file (a page's Markdown, an attachment). A path is found from the root a folder at a time, each folder
/// listing its children through the source's caches; a child is found by its name in either case.
/// </summary>
internal abstract class CoNode(string name)
{
    public string Name { get; } = name;

    public abstract VfsEntry Entry { get; }
}

internal abstract class CoFolder(string name, DateTimeOffset? modified = null, DateTimeOffset? created = null) : CoNode(name)
{
    public DateTimeOffset? Modified { get; } = modified;

    public DateTimeOffset? Created { get; } = created;

    public override VfsEntry Entry => VfsEntry.Directory(Name, Modified, Created);

    public abstract Task<IReadOnlyList<CoNode>> ListAsync(ConfluenceSource source, CancellationToken ct);

    /// <summary>The child named <paramref name="name"/> (in either case), or null.</summary>
    public virtual async Task<CoNode?> FindAsync(ConfluenceSource source, string name, CancellationToken ct)
        => (await ListAsync(source, ct).ConfigureAwait(false)).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

internal abstract class CoFile(string name, long length, DateTimeOffset? modified, DateTimeOffset? created = null) : CoNode(name)
{
    public long Length { get; } = length;

    public override VfsEntry Entry => VfsEntry.File(Name, Length, modified, created ?? modified);

    public abstract Task<IVfsFile> OpenAsync(ConfluenceSource source, CancellationToken ct);
}

/// <summary>A file the source makes itself and holds in memory (a page as Markdown).</summary>
internal sealed class CoText(string name, byte[] content, DateTimeOffset? modified, DateTimeOffset? created = null) : CoFile(name, content.Length, modified, created)
{
    public override Task<IVfsFile> OpenAsync(ConfluenceSource source, CancellationToken ct) => Task.FromResult<IVfsFile>(new MemoryFile(content, "a file made from Confluence's data"));
}

/// <summary>The whole server: a folder per space the account can read, then Personal spaces and Archived spaces where shown.</summary>
internal sealed class ConfluenceServerRoot() : CoFolder("")
{
    public override async Task<IReadOnlyList<CoNode>> ListAsync(ConfluenceSource source, CancellationToken ct)
    {
        var spaces = (await source.SpacesAsync(ct).ConfigureAwait(false)).Where(s => !s.Personal && !s.Archived).ToList();
        var children = ConfluenceSpaceList.Folders(spaces).ToList();
        if (source.Settings.PersonalSpaces)
        {
            children.Add(new ConfluenceSpaceList(ConfluenceDocuments.PersonalFolder, archived: false));
        }

        if (source.Settings.ArchivedSpaces)
        {
            children.Add(new ConfluenceSpaceList(ConfluenceDocuments.ArchivedFolder, archived: true));
        }

        // a space named like the two lists above keeps its own name, and they theirs: names are unique already (a key in each)
        return children;
    }
}

/// <summary>The personal spaces the account can read, or the archived ones.</summary>
internal sealed class ConfluenceSpaceList(string name, bool archived) : CoFolder(name)
{
    public override async Task<IReadOnlyList<CoNode>> ListAsync(ConfluenceSource source, CancellationToken ct)
    {
        var spaces = await source.SpacesAsync(ct).ConfigureAwait(false);
        return Folders(spaces.Where(s => archived ? s.Archived : s.Personal && !s.Archived).ToList()).ToList();
    }

    public static IEnumerable<CoNode> Folders(IReadOnlyList<ConfluenceSpace> spaces)
    {
        var names = AtlassianNames.Unique(spaces.Select(ConfluenceDocuments.SpaceFolderName).ToList());
        return spaces.Select((s, i) => (CoNode)new ConfluenceSpaceFolder(s, names[i]));
    }
}

/// <summary>A space: its top-level pages (the home page among them), each a folder, and its blog where shown.</summary>
internal sealed class ConfluenceSpaceFolder(ConfluenceSpace space, string name) : CoFolder(name)
{
    public ConfluenceSpace Space { get; } = space;

    public override async Task<IReadOnlyList<CoNode>> ListAsync(ConfluenceSource source, CancellationToken ct)
    {
        // by title, as the walk names them, so a name taken twice is numbered alike from either read
        var pages = (await source.TopPagesAsync(Space.Key, ct).ConfigureAwait(false)).OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
        var reserved = new List<string>();
        var blog = source.Settings.BlogPosts && await source.HasBlogAsync(Space.Key, ct).ConfigureAwait(false);
        if (blog)
        {
            reserved.Add(ConfluenceDocuments.BlogFolder);
        }

        var names = AtlassianNames.Unique(pages.Select(ConfluenceDocuments.PageFolderName).ToList(), reserved);
        var children = pages.Select((p, i) => (CoNode)new ConfluencePageFolder(p, names[i], parentId: null)).ToList();
        if (blog)
        {
            children.Add(new ConfluenceBlogFolder(Space));
        }

        return children;
    }
}

/// <summary>A space's blog: a folder per post, named by the day it was posted.</summary>
internal sealed class ConfluenceBlogFolder(ConfluenceSpace space) : CoFolder(ConfluenceDocuments.BlogFolder)
{
    public ConfluenceSpace Space { get; } = space;

    public override async Task<IReadOnlyList<CoNode>> ListAsync(ConfluenceSource source, CancellationToken ct)
    {
        var posts = await source.BlogPostsAsync(Space.Key, ct).ConfigureAwait(false);
        var names = AtlassianNames.Unique(posts.Select(ConfluenceDocuments.PageFolderName).ToList());
        return posts.Select((p, i) => (CoNode)new ConfluencePageFolder(p, names[i], parentId: null, blog: true)).ToList();
    }
}

/// <summary>
/// A page (or blog post): its Markdown, named as the folder with <c>.md</c>, its attachments, and its child pages'
/// folders. The content a listing names it from carries no body; the body is read when the folder is listed.
/// </summary>
internal sealed class ConfluencePageFolder(ConfluenceContent content, string name, string? parentId, bool blog = false) : CoFolder(name, content.Changed, content.Created)
{
    public ConfluenceContent Content { get; } = content;

    /// <summary>The parent page's id (null at a space's top or in its blog): the pages read together with this one.</summary>
    public string? ParentId { get; } = parentId;

    public bool Blog { get; } = blog;

    /// <summary>With its key (<see cref="ConfluenceKeys"/>): the listing that names it is enough to find it again.</summary>
    public override VfsEntry Entry => base.Entry with { Key = ConfluenceKeys.Of(this) };

    public override Task<IReadOnlyList<CoNode>> ListAsync(ConfluenceSource source, CancellationToken ct) => source.PageChildrenAsync(this, ct);

    /// <summary>A child page's folder from the pages' names alone, no body or comments read; anything else from the whole listing.</summary>
    public override async Task<CoNode?> FindAsync(ConfluenceSource source, string name, CancellationToken ct)
        => (name.Equals(Name + ".md", StringComparison.OrdinalIgnoreCase) ? null : await source.ChildFolderAsync(this, name, ct).ConfigureAwait(false))
            ?? await base.FindAsync(source, name, ct).ConfigureAwait(false);
}

/// <summary>An attached file, read from Confluence as it is opened (spooled), its size from the attachment's own record.</summary>
internal sealed class ConfluenceAttachmentFile(ConfluenceAttachment attachment, string name) : CoFile(name, attachment.Size, attachment.Changed)
{
    public ConfluenceAttachment Attachment { get; } = attachment;

    /// <summary>With its key (<see cref="ConfluenceKeys"/>): the listing that names it is enough to fetch it.</summary>
    public override VfsEntry Entry => base.Entry with { Key = ConfluenceKeys.Of(Attachment) };

    public override Task<IVfsFile> OpenAsync(ConfluenceSource source, CancellationToken ct) => source.OpenAttachmentAsync(Attachment, ct);
}

/// <summary>
/// What a listing keeps of a page's folder and an attachment (<see cref="VfsEntry.Key"/>): what finding them again takes, kept
/// with the listing and saved with it, so an open or a listing goes straight to the thing however deep it is.
/// </summary>
internal static class ConfluenceKeys
{
    private const char Separator = '\n';

    public static string Of(ConfluencePageFolder folder)
        => string.Join(Separator, "p", folder.Content.Id, folder.Content.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), folder.ParentId ?? "", folder.Blog ? "1" : "0", folder.Content.SpaceKey);

    public static string Of(ConfluenceAttachment attachment)
        => string.Join(Separator, "a", attachment.Id, attachment.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), attachment.ContainerId ?? "", attachment.FileName, attachment.Download ?? "");

    /// <summary>The thing an entry of a held listing names, from its key; null for an entry without one.</summary>
    public static CoNode? Node(VfsEntry entry)
    {
        var parts = entry.Key?.Split(Separator);
        switch (parts)
        {
            case ["p", var id, var version, var parent, var blog, var space]:
                var content = new ConfluenceContent(id, blog == "1" ? "blogpost" : "page", entry.Name, space, int.Parse(version, System.Globalization.CultureInfo.InvariantCulture), entry.Modified, null, entry.Created, null, [], parent.Length == 0 ? null : parent, null, null, null);
                return new ConfluencePageFolder(content, entry.Name, content.ParentId, blog == "1");
            case ["a", var id, var version, var container, var file, var download]:
                return new ConfluenceAttachmentFile(new ConfluenceAttachment(id, file, entry.Length, null, int.Parse(version, System.Globalization.CultureInfo.InvariantCulture), entry.Modified, download.Length == 0 ? null : download, container.Length == 0 ? null : container), entry.Name);
            default:
                return null;
        }
    }
}
