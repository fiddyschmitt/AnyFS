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
    public async Task<CoNode?> FindAsync(ConfluenceSource source, string name, CancellationToken ct)
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

    public override Task<IReadOnlyList<CoNode>> ListAsync(ConfluenceSource source, CancellationToken ct) => source.PageChildrenAsync(this, ct);
}

/// <summary>An attached file, read from Confluence as it is opened (spooled), its size from the attachment's own record.</summary>
internal sealed class ConfluenceAttachmentFile(ConfluenceAttachment attachment, string name) : CoFile(name, attachment.Size, attachment.Changed)
{
    public ConfluenceAttachment Attachment { get; } = attachment;

    public override Task<IVfsFile> OpenAsync(ConfluenceSource source, CancellationToken ct) => source.OpenAttachmentAsync(Attachment, ct);
}
