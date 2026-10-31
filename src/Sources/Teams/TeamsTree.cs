using AnyFS.Sdk;

namespace AnyFS.Sources.Teams;

/// <summary>
/// One thing in a Teams source's tree (spec 7.9): a folder (the root, a team, a channel, its posts, the Chats folder, a
/// channel post, a chat, their attachments and recordings) or a file (a post or a chat as Markdown, a team's or channel's
/// details, a shared file, an image, a recording). A path is found from the root a folder at a time, each folder finding
/// its child through the source. Names are compared in either case, as Windows does.
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

    public abstract Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct);

    /// <summary>A child by name, for finding a path: listed and looked up (a chat finds its attachments without building its chat.md).</summary>
    public virtual async Task<TmNode?> FindAsync(TeamsSource source, string name, CancellationToken ct)
        => (await ListAsync(source, ct).ConfigureAwait(false)).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
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

/// <summary>
/// The source's root: a folder per team, then the Chats folder, Other groups (while SharePoint's token or Graph's is in
/// hand), all-groups.md (every group) and groups.md (the account's), calls.md (the call history) and activity.md (the
/// activity feed) where the account has them.
/// </summary>
internal sealed class RootFolder : TmFolder
{
    // the root's own folders and files: a team of one of these names is numbered
    private static readonly string[] Reserved = [ChatsFolder.FolderName, OtherGroupsFolder.FolderName, "all-groups.md", "groups.md", "calls.md", "activity.md"];

    public RootFolder() : base("")
    {
    }

    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        var teams = source.Settings.ChannelMessages || source.Settings.Files ? await source.TeamsAsync(ct).ConfigureAwait(false) : [];
        var names = TeamsNames.Unique(teams.Select(t => t.DisplayName).ToList(), Reserved);
        var children = teams.Select((t, i) => (TmNode)new TeamFolder(t, names[i])).ToList();
        if (source.Settings.Chats)
        {
            children.Add(new ChatsFolder());
        }

        if (source.Settings.Groups)
        {
            // Other groups and all-groups.md whatever token is in hand: the one waits for what opens it when listed, the other
            // says what it is waiting on, so a listing has the same names the first time as the tenth
            children.Add(new OtherGroupsFolder());
            children.Add(new MarkdownFile("all-groups.md", source.AllGroupsMarkdown(), null));
            children.Add(new MarkdownFile("groups.md", source.GroupsMarkdown(), null));
        }

        // built behind the listings, the first build waited for: shown where the account has them
        if (source.Settings.CallsAndActivity)
        {
            var calls = source.CallsMarkdownAsync(ct);
            var activity = source.ActivityMarkdownAsync(ct);
            if (await calls.ConfigureAwait(false) is { } callHistory)
            {
                children.Add(new MarkdownFile("calls.md", callHistory, null));
            }

            if (await activity.ConfigureAwait(false) is { } feed)
            {
                children.Add(new MarkdownFile("activity.md", feed, null));
            }
        }

        return children;
    }
}

/// <summary>
/// Every group site the account can open beyond its teams above (public groups it has not joined among them): a folder
/// each, of the group's library. Its listing waits for what opens it (SharePoint's token or Graph's, asked for behind the
/// listings) and for the group sites to be read; walked as the others are.
/// </summary>
internal sealed class OtherGroupsFolder() : TmFolder(FolderName)
{
    public const string FolderName = "Other groups";

    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        var groups = await source.OtherGroupsAsync(ct).ConfigureAwait(false);
        var names = TeamsNames.Unique(groups.Select(g => g.Name).ToList());
        return groups.Select((g, i) => (TmNode)new GroupFolder(g, names[i])).ToList();
    }
}

/// <summary>A group the account has no team of here: its site's library, as the library has it (its site found from the group where the list did not name it; none while it has none).</summary>
internal sealed class GroupFolder(GroupSite group, string name) : TmFolder(name, group.Created)
{
    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        await source.RequireFilesAsync(ct).ConfigureAwait(false);
        return (group.SiteUrl ?? (group.GroupId is { } id ? await source.Api.SiteOfGroupAsync(id, ct).ConfigureAwait(false) : null)) is { } site
            ? await LibraryFolder.ChildrenAsync(source, site, "", ct).ConfigureAwait(false)
            : [];
    }
}

/// <summary>A team: a folder per channel, and <c>team.md</c>, the team's details.</summary>
internal sealed class TeamFolder(TeamInfo team, string name) : TmFolder(name)
{
    public const string DetailsFile = "team.md";

    public TeamInfo Team { get; } = team;

    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        var names = TeamsNames.Unique(Team.Channels.Select(c => c.DisplayName).ToList(), [DetailsFile]);
        var children = Team.Channels.Select((c, i) => (TmNode)new ChannelFolder(Team, c, names[i])).ToList();
        var general = await source.TeamDetailsAsync(Team, ct).ConfigureAwait(false);
        children.Add(new MarkdownFile(DetailsFile, TeamsText.Team(Team, general, TeamsLinks.Team(source.LinkHost, Team)), null));
        return children;
    }
}

/// <summary>
/// A channel: its posts, its files (the channel's folder of its team's SharePoint library, whose listing waits for
/// SharePoint's token or Graph's), and <c>channel.md</c>, the channel's details.
/// </summary>
internal sealed class ChannelFolder(TeamInfo team, ChannelInfo channel, string name) : TmFolder(name)
{
    public TeamInfo Team { get; } = team;

    public ChannelInfo Channel { get; } = channel;

    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        var children = new List<TmNode>();
        if (source.Settings.ChannelMessages)
        {
            children.Add(new PostsFolder(Team, Channel));
        }

        if (source.Settings.Files)
        {
            children.Add(new ChannelFilesFolder(Team, Channel));
        }

        var details = await source.ThreadDetailsAsync(Channel.Id, ct).ConfigureAwait(false);
        children.Add(new MarkdownFile("channel.md", TeamsText.Channel(Channel, Team, details, source.NameOf, TeamsLinks.Channel(source.LinkHost, Team, Channel)), Channel.Created));
        return children;
    }
}

/// <summary>
/// A channel's files: its folder of its team's SharePoint library, or of a private or shared channel's own site, as the
/// channel's thread names it; empty while the team has no site. Its listing waits for what reads it (SharePoint's token
/// or Graph's), and fails where none comes.
/// </summary>
internal sealed class ChannelFilesFolder(TeamInfo team, ChannelInfo channel) : TmFolder("files")
{
    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        await source.RequireFilesAsync(ct).ConfigureAwait(false);
        var thread = await source.ThreadDetailsAsync(channel.Id, ct).ConfigureAwait(false);
        if (await source.Api.ChannelFilesAsync(team, channel, thread, ct).ConfigureAwait(false) is not { } where)
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
        await source.RequireFilesAsync(ct).ConfigureAwait(false);
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

/// <summary>An inline image of the length its first reading measured: served as that reading kept it, else read again from Teams' media service when opened.</summary>
internal sealed class ImageFile(string name, string src, long length, DateTimeOffset? modified) : TmFile(name, length, modified)
{
    public override async Task<IVfsFile> OpenAsync(TeamsSource source, CancellationToken ct)
        => new MemoryFile(await source.ImageBytesAsync(src, ct).ConfigureAwait(false), "an inline image from Teams");
}

/// <summary>A conversation's attachments: the files its messages share and its inline images, read with the conversation.</summary>
internal sealed class AttachmentsFolder(IReadOnlyList<TmNode> attachments, DateTimeOffset? modified) : TmFolder(FolderName, modified)
{
    public const string FolderName = "attachments";

    public override Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct) => Task.FromResult(attachments);
}

/// <summary>A chat's meeting recordings: a file each, read with the chat.</summary>
internal sealed class RecordingsFolder(IReadOnlyList<TmNode> recordings, DateTimeOffset? modified) : TmFolder(FolderName, modified)
{
    public const string FolderName = "recordings";

    public override Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct) => Task.FromResult(recordings);
}

/// <summary>A meeting recording (an MP4), read in ranges from Teams' media service as it is read.</summary>
internal sealed class RecordingFile(string name, string video, long length, DateTimeOffset? modified) : TmFile(name, length, modified)
{
    public override Task<IVfsFile> OpenAsync(TeamsSource source, CancellationToken ct) => Task.FromResult(source.OpenMedia(video, Name, Entry.Length));
}

/// <summary>
/// A channel's posts: a folder per thread (the opening message and its replies), back to the channel's start or the window.
/// A busy channel's latest 2,000 messages are read when it is listed and the rest of its history read on, the listing
/// waiting for it (an opening message read brings its thread's name). Its stamp for the walks is the channel's version,
/// once its history is whole (<see cref="TeamsSource.StampAsync"/>).
/// </summary>
internal sealed class PostsFolder(TeamInfo team, ChannelInfo channel) : TmFolder("posts", null), IHistoryFolder
{
    public string ThreadId => channel.Id;

    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
        => ThreadFolder.Of(source, team, channel, await source.ThreadsAsync(channel.Id, ct).ConfigureAwait(false));
}

/// <summary>
/// One channel post: <c>post.md</c>, the opening message and its replies, its <c>attachments</c> (the files they share,
/// their inline images) where it has any, and a channel meeting's <c>recordings</c>; built when the post itself is listed
/// (not when its posts folder is). Its stamp for the walks is made of its messages (<see cref="TeamsStamps.Of"/>).
/// </summary>
internal sealed class ThreadFolder(TeamInfo team, ChannelInfo channel, string name, IReadOnlyList<TeamsMessage> thread) : TmFolder(name, thread[0].Composed)
{
    public IReadOnlyList<TeamsMessage> Thread { get; } = thread;

    /// <summary>A folder per thread, named by its time, its author and its first line, twins numbered.</summary>
    public static List<TmNode> Of(TeamsSource source, TeamInfo team, ChannelInfo channel, IReadOnlyList<IReadOnlyList<TeamsMessage>> threads)
    {
        var names = TeamsNames.Unique(threads.Select(t => TeamsText.ThreadName(t[0], source.NameOf)).ToList());
        return threads.Select((t, i) => (TmNode)new ThreadFolder(team, channel, names[i], t)).ToList();
    }

    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        var (attachments, names) = await source.AttachmentsAsync(Thread, ct).ConfigureAwait(false);
        var (_, recordings, recorded) = await source.RecordingsAsync(Thread, ct).ConfigureAwait(false);
        var children = new List<TmNode>
        {
            new MarkdownFile("post.md", TeamsText.Post(Thread, source.NameOf, names with { Recordings = recorded }, TeamsLinks.Post(source.LinkHost, team, channel, Thread)), Thread[0].Composed),
        };
        if (attachments.Count > 0)
        {
            children.Add(new AttachmentsFolder(attachments, Thread[^1].Composed));
        }

        if (recordings.Count > 0)
        {
            children.Add(new RecordingsFolder(recordings, Thread[^1].Composed));
        }

        return children;
    }
}

/// <summary>The account's chats: a folder per chat, named for it (<see cref="TeamsSource.ChatName"/>), twins numbered; those hidden in Teams among them.</summary>
internal sealed class ChatsFolder() : TmFolder(FolderName)
{
    public const string FolderName = "Chats";

    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        var chats = await source.ChatsAsync(ct).ConfigureAwait(false);
        var names = TeamsNames.Unique(chats.Select(source.ChatName).ToList());
        return chats.Select((c, i) => (TmNode)new ChatFolder(c, names[i])).ToList();
    }
}

/// <summary>
/// One chat: <c>chat.md</c>, the whole conversation, its <c>attachments</c> where it has any, a meeting's chat its
/// <c>meeting.md</c>, and a chat with recordings its <c>recordings</c>. A long chat's latest 2,000 messages are read when it
/// is listed and the rest of its history read on, the listing waiting for it. Its stamp for the walks is the chat's
/// version, once its history is whole (<see cref="TeamsSource.StampAsync"/>).
/// </summary>
internal sealed class ChatFolder(ChatInfo chat, string name) : TmFolder(name, chat.LastUpdated), IHistoryFolder
{
    public ChatInfo Chat { get; } = chat;

    public string ThreadId => Chat.Id;

    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        var parts = await source.ChatAsync(Chat, ct).ConfigureAwait(false);
        var children = new List<TmNode> { new MarkdownFile("chat.md", parts.Markdown, Chat.LastUpdated) };
        if (parts.Attachments.Count > 0)
        {
            children.Add(new AttachmentsFolder(parts.Attachments, Chat.LastUpdated));
        }

        if (parts.Meeting is { } meeting)
        {
            children.Add(new MarkdownFile("meeting.md", meeting, Chat.LastUpdated));
        }

        if (parts.Recordings.Count > 0)
        {
            children.Add(new RecordingsFolder(parts.Recordings, Chat.LastUpdated));
        }

        return children;
    }

    /// <summary>A child by name: its attachments or its recordings found without building chat.md (a folder of images opened is a find per image).</summary>
    public override async Task<TmNode?> FindAsync(TeamsSource source, string name, CancellationToken ct)
    {
        var attachments = name.Equals(AttachmentsFolder.FolderName, StringComparison.OrdinalIgnoreCase);
        if (!attachments && !name.Equals(RecordingsFolder.FolderName, StringComparison.OrdinalIgnoreCase))
        {
            return await base.FindAsync(source, name, ct).ConfigureAwait(false);
        }

        var files = await source.ChatFilesAsync(Chat, ct).ConfigureAwait(false);
        if (attachments)
        {
            return files.Attachments.Count > 0 ? new AttachmentsFolder(files.Attachments, Chat.LastUpdated) : null;
        }

        return files.Recordings.Count > 0 ? new RecordingsFolder(files.Recordings, Chat.LastUpdated) : null;
    }
}
