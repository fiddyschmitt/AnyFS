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
    public override Task<IVfsFile> OpenAsync(TeamsSource source, CancellationToken ct) => Task.FromResult<IVfsFile>(new MemoryFile(content, "a Teams conversation as Markdown"));
}

/// <summary>The source's root: a folder per team, then the Chats folder.</summary>
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

        return children;
    }
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

/// <summary>A channel: its posts (and, with the files feature, its Files folder — added with that feature).</summary>
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

        return Task.FromResult<IReadOnlyList<TmNode>>(children);
    }
}

/// <summary>A channel's posts: a folder per thread (the opening message and its replies). Not walked: the messages are a call.</summary>
internal sealed class PostsFolder(ChannelInfo channel) : TmFolder("posts", null)
{
    public override bool Walked => false;

    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        var threads = await source.ThreadsAsync(channel.Id, ct).ConfigureAwait(false);
        var names = TeamsNames.Unique(threads.Select(t => TeamsText.ThreadName(t[0], source.NameOf)).ToList());
        return threads.Select((t, i) => (TmNode)new ThreadFolder(names[i], t, source)).ToList();
    }
}

/// <summary>One channel post: <c>post.md</c>, the opening message and its replies.</summary>
internal sealed class ThreadFolder : TmFolder
{
    private readonly IReadOnlyList<TeamsMessage> _thread;
    private readonly byte[] _markdown;

    public ThreadFolder(string name, IReadOnlyList<TeamsMessage> thread, TeamsSource source)
        : base(name, thread[0].Composed)
    {
        _thread = thread;
        _markdown = TeamsText.Post(thread, source.NameOf);
    }

    public override bool Walked => false;

    public override Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TmNode>>([new MarkdownFile("post.md", _markdown, _thread[0].Composed)]);
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

/// <summary>One chat: <c>chat.md</c>, the whole conversation. Not walked: the messages are a call.</summary>
internal sealed class ChatFolder(ChatInfo chat, string name) : TmFolder(name, chat.LastUpdated)
{
    public ChatInfo Chat { get; } = chat;

    public override bool Walked => false;

    public override async Task<IReadOnlyList<TmNode>> ListAsync(TeamsSource source, CancellationToken ct)
    {
        var markdown = await source.ChatMarkdownAsync(Chat, ct).ConfigureAwait(false);
        return [new MarkdownFile("chat.md", markdown, Chat.LastUpdated)];
    }
}
