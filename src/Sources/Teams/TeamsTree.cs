using AnyFS.Sdk;

namespace AnyFS.Sources.Teams;

/// <summary>
/// One thing in a Teams source's tree (spec 7.9): a folder (the root, a team, a channel, the Chats folder, a channel post
/// or a chat) or a file (a post or chat as Markdown). A path is found from the root a listing at a time, each folder
/// listing its children through the source. Names are compared in either case, as Windows does.
/// </summary>
internal abstract class TmNode(string name)
{
    public string Name { get; } = name;

    public abstract VfsEntry Entry { get; }
}

internal abstract class TmFolder(string name, DateTimeOffset? modified = null) : TmNode(name)
{
    public DateTimeOffset? Modified { get; } = modified;

    public override VfsEntry Entry => VfsEntry.Directory(Name, Modified);

    /// <summary>Whether "Count files on start" walks into it. A conversation's messages cost their own calls, so those folders are not walked (listed when opened).</summary>
    public virtual bool Walked => true;

    public abstract Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct);
}

internal abstract class TmFile(string name, long length, DateTimeOffset? modified) : TmNode(name)
{
    public override VfsEntry Entry => VfsEntry.File(Name, length, modified, modified);

    public abstract Task<IVfsFile> OpenAsync(TeamsSource source, CancellationToken ct);
}

/// <summary>A file the source makes itself and holds in memory (a post or a chat as Markdown).</summary>
internal sealed class MarkdownFile(string name, byte[] content, DateTimeOffset? modified) : TmFile(name, content.Length, modified)
{
    /// <summary>The bytes the listing measured: what an open of this listing's file must serve (<see cref="TeamsSource"/> keeps them).</summary>
    public byte[] Content { get; } = content;

    public override Task<IVfsFile> OpenAsync(TeamsSource source, CancellationToken ct) => Task.FromResult<IVfsFile>(Open(Content));

    public static IVfsFile Open(byte[] content) => new MemoryFile(content, "a Teams conversation as Markdown");
}

/// <summary>The source's root: a folder per team, then the Chats folder, Other groups (while SharePoint's token or Graph's is in hand) and groups.md.</summary>
internal sealed class RootFolder : TmFolder
{
    public RootFolder() : base("")
    {
    }

    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        var teams = source.Settings.ChannelMessages || source.Settings.Files ? await source.TeamsAsync(ct).ConfigureAwait(false) : [];
        var names = TeamsNames.Unique(teams.Select(t => t.DisplayName).ToList());
        var children = teams.Select((t, i) => (TmNode)new TeamFolder(t, names[i])).ToList();
        if (source.Settings.Chats)
        {
            children.Add(new ChatsFolder());
        }

        if (source.Settings.Groups)
        {
            // Other groups only while what opens it is in hand: nothing waits for SharePoint's token or Graph's
            if (source.FilesReady())
            {
                children.Add(new OtherGroupsFolder());
            }

            children.Add(new MarkdownFile("groups.md", source.GroupsMarkdown(), null));
        }

        return children;
    }
}

/// <summary>
/// Every group site the account can open beyond its teams above (public groups it has not joined among them): a folder
/// each, of the group's library. Listed while SharePoint's token or Graph's is in hand; walked as the others are.
/// </summary>
internal sealed class OtherGroupsFolder() : TmFolder("Other groups")
{
    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        var groups = await source.OtherGroupsAsync(ct).ConfigureAwait(false);
        var names = TeamsNames.Unique(groups.Select(g => g.Name).ToList());
        return groups.Select((g, i) => (TmNode)new GroupFolder(g, names[i])).ToList();
    }
}

/// <summary>A group the account has no team of here: its site's library, as the library has it.</summary>
internal sealed class GroupFolder(GroupSite group, string name) : TmFolder(name, group.Created)
{
    public override Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct) => LibraryFolder.ChildrenAsync(source, group.SiteUrl, "", ct);
}

/// <summary>A team: a folder per channel.</summary>
internal sealed class TeamFolder(TeamInfo team, string name) : TmFolder(name)
{
    public TeamInfo Team { get; } = team;

    public override Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        var names = TeamsNames.Unique(Team.Channels.Select(c => c.DisplayName).ToList());
        return Task.FromResult<IReadOnlyList<TmNode>>(Team.Channels.Select((c, i) => (TmNode)new ChannelFolder(Team, c, names[i])).ToList());
    }
}

/// <summary>A channel: its posts, and its files (the channel's folder of its team's SharePoint library) while SharePoint's token or Graph's is in hand.</summary>
internal sealed class ChannelFolder(TeamInfo team, ChannelInfo channel, string name) : TmFolder(name)
{
    public TeamInfo Team { get; } = team;

    public ChannelInfo Channel { get; } = channel;

    public override Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        var children = new List<TmNode>();
        if (source.Settings.ChannelMessages)
        {
            children.Add(new PostsFolder(Channel));
        }

        // the files only while what reads them is in hand: nothing waits for SharePoint's token or Graph's
        if (source.Settings.Files && source.FilesReady())
        {
            children.Add(new ChannelFilesFolder(Team, Channel));
        }

        return Task.FromResult<IReadOnlyList<TmNode>>(children);
    }
}

/// <summary>A channel's files: its folder of its team's SharePoint library (or of a private channel's own site); empty while the team has no site.</summary>
internal sealed class ChannelFilesFolder(TeamInfo team, ChannelInfo channel) : TmFolder("files")
{
    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        if (await source.Api.ChannelFilesAsync(team, channel, ct).ConfigureAwait(false) is not { } where)
        {
            return [];
        }

        return await LibraryFolder.ChildrenAsync(source, where.SiteUrl, where.Path, ct).ConfigureAwait(false);
    }
}

/// <summary>A folder of a SharePoint library (in a channel's files): its folders and files, as the library has them.</summary>
internal sealed class LibraryFolder(string siteUrl, string path, string name, DateTimeOffset? modified) : TmFolder(name, modified)
{
    public override Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct) => ChildrenAsync(source, siteUrl, path, ct);

    public static async Task<IReadOnlyList<TmNode>> ChildrenAsync(TeamsSource source, string siteUrl, string path, CancellationToken ct)
    {
        var entries = await source.Api.FolderAsync(siteUrl, path, ct).ConfigureAwait(false);
        return entries.Select(e =>
        {
            var child = path.Length == 0 ? e.Name : $"{path}/{e.Name}";
            return e.IsFolder ? (TmNode)new LibraryFolder(siteUrl, child, e.Name, e.Modified) : new LibraryFile(e.Name, siteUrl, child, e.Length, e.Modified);
        }).ToList();
    }
}

/// <summary>
/// A file in SharePoint or OneDrive (a channel's file, or one a message shares, with its own address), read in ranges through
/// a fresh download address.
/// </summary>
internal sealed class LibraryFile(string name, string siteUrl, string path, long length, DateTimeOffset? modified, string? url = null) : TmFile(name, length, modified)
{
    public override Task<IVfsFile> OpenAsync(TeamsSource source, CancellationToken ct) => source.OpenLibraryFileAsync(siteUrl, path, url, Entry.Length, ct);
}

/// <summary>An inline image, read from Teams' media service when its conversation was, held in memory.</summary>
internal sealed class ImageFile(string name, TeamsImage image, DateTimeOffset? modified) : TmFile(name, image.Bytes.Length, modified)
{
    public override Task<IVfsFile> OpenAsync(TeamsSource source, CancellationToken ct) => Task.FromResult<IVfsFile>(new MemoryFile(image.Bytes, "an inline image from Teams"));
}

/// <summary>A conversation's attachments: the files its messages share and its inline images. Not walked: read with the conversation.</summary>
internal sealed class AttachmentsFolder(IReadOnlyList<TmNode> attachments, DateTimeOffset? modified) : TmFolder("attachments", modified)
{
    public override bool Walked => false;

    public override Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct) => Task.FromResult(attachments);
}

/// <summary>A channel's posts: a folder per thread (the opening message and its replies). Not walked: the messages are a call.</summary>
internal sealed class PostsFolder(ChannelInfo channel) : TmFolder("posts", null)
{
    public override bool Walked => false;

    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        var threads = await source.ThreadsAsync(channel.Id, ct).ConfigureAwait(false);
        var names = TeamsNames.Unique(threads.Select(t => TeamsText.ThreadName(t[0], source.NameOf)).ToList());
        return threads.Select((t, i) => (TmNode)new ThreadFolder(names[i], t)).ToList();
    }
}

/// <summary>
/// One channel post: <c>post.md</c>, the opening message and its replies, and its <c>attachments</c> (the files they share,
/// their inline images) where it has any; built when the post itself is listed (not when its posts folder is).
/// </summary>
internal sealed class ThreadFolder(string name, IReadOnlyList<TeamsMessage> thread) : TmFolder(name, thread[0].Composed)
{
    public override bool Walked => false;

    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        var (attachments, names) = await source.AttachmentsAsync(thread, ct).ConfigureAwait(false);
        var children = new List<TmNode> { new MarkdownFile("post.md", TeamsText.Post(thread, source.NameOf, names), thread[0].Composed) };
        if (attachments.Count > 0)
        {
            children.Add(new AttachmentsFolder(attachments, thread[^1].Composed));
        }

        return children;
    }
}

/// <summary>The account's chats: a folder per chat.</summary>
internal sealed class ChatsFolder() : TmFolder("Chats")
{
    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        var chats = await source.ChatsAsync(ct).ConfigureAwait(false);
        var names = TeamsNames.Unique(chats.Select(source.ChatName).ToList());
        return chats.Select((c, i) => (TmNode)new ChatFolder(c, names[i])).ToList();
    }
}

/// <summary>One chat: <c>chat.md</c>, the whole conversation, and its <c>attachments</c> where it has any. Not walked: the messages are a call.</summary>
internal sealed class ChatFolder(ChatInfo chat, string name) : TmFolder(name, chat.LastUpdated)
{
    public ChatInfo Chat { get; } = chat;

    public override bool Walked => false;

    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        var (markdown, attachments) = await source.ChatAsync(Chat, ct).ConfigureAwait(false);
        var children = new List<TmNode> { new MarkdownFile("chat.md", markdown, Chat.LastUpdated) };
        if (attachments.Count > 0)
        {
            children.Add(new AttachmentsFolder(attachments, Chat.LastUpdated));
        }

        return children;
    }
}
