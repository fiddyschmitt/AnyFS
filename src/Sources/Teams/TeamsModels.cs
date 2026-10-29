namespace AnyFS.Sources.Teams;

/// <summary>A team the account is in: the aggregator's <c>teams[]</c> entry (spec 7.9, spike 18).</summary>
/// <param name="Id">The team's thread id (<c>19:…@thread.tacv2</c>).</param>
/// <param name="GroupId">The Microsoft 365 group behind the team, from which its SharePoint site is found (files).</param>
internal sealed record TeamInfo(string Id, string DisplayName, string? GroupId, IReadOnlyList<ChannelInfo> Channels);

/// <summary>A channel of a team: its own conversation thread.</summary>
/// <param name="Id">The channel's thread id, which is also the conversation id its messages are read by.</param>
/// <param name="IsPrivate">A private (or shared) channel, whose files live in a site of its own.</param>
internal sealed record ChannelInfo(string Id, string DisplayName, bool IsGeneral, bool IsPrivate);

internal enum ChatKind
{
    OneOnOne,
    Group,
    Meeting,
}

/// <summary>A chat the account is in: the aggregator's <c>chats[]</c> entry.</summary>
/// <param name="Title">The chat's own topic, where it has one; null for a 1:1 or an untitled group (then named for its members).</param>
/// <param name="MemberMris">The other people's mris (the account's own left out), for naming a 1:1 or group chat.</param>
internal sealed record ChatInfo(string Id, string? Title, ChatKind Kind, IReadOnlyList<string> MemberMris, DateTimeOffset? LastUpdated);

/// <summary>One message of a conversation (a channel's or a chat's): the chat service's <c>messages[]</c> entry.</summary>
/// <param name="RootId">The opening message of its thread (channel posts are grouped by this); null or itself for a root.</param>
/// <param name="FromMri">Who sent it (<c>8:orgid:&lt;id&gt;</c>); null for a system message with no sender.</param>
/// <param name="MessageType">RichText/Html, Text, or ThreadActivity/* (a system message: a member added, a call, a rename).</param>
/// <param name="Html">The message body as HTML (RichText/Html) or plain text (Text); null for a system activity.</param>
internal sealed record TeamsMessage(
    string Id,
    string? RootId,
    string? ParentId,
    string? FromMri,
    DateTimeOffset? Composed,
    string MessageType,
    string? Html,
    string? FromDisplayName)
{
    /// <summary>When it was last edited (<c>properties.edittime</c>); null if never.</summary>
    public DateTimeOffset? Edited { get; init; }

    /// <summary>When it was deleted (<c>properties.deletetime</c>): its content is gone, its place in the conversation stays.</summary>
    public DateTimeOffset? Deleted { get; init; }

    /// <summary>Its reactions (<c>properties.emotions</c>), each with who reacted; empty when none.</summary>
    public IReadOnlyList<Reaction> Reactions { get; init; } = [];

    /// <summary>A real message people wrote, not a system activity (member added, call ended, channel renamed).</summary>
    public bool IsContent => MessageType.StartsWith("RichText", StringComparison.OrdinalIgnoreCase) || MessageType.Equals("Text", StringComparison.OrdinalIgnoreCase);

    /// <summary>Its place in its thread: a reply points at its root, a root at itself (or carries none).</summary>
    public string ThreadId => string.IsNullOrEmpty(RootId) ? Id : RootId!;
}

/// <summary>A reaction on a message: its key (like, heart, laugh, surprised, sad, angry, or an emoji named by its code point, <c>1f600_grinningface</c>) and who reacted.</summary>
internal sealed record Reaction(string Key, IReadOnlyList<string> Mris);

/// <summary>The account's teams (each with its channels) and its chats: one answer of the aggregator holds both.</summary>
internal sealed record TeamsStructure(IReadOnlyList<TeamInfo> Teams, IReadOnlyList<ChatInfo> Chats);

/// <summary>A page of a conversation's messages and the link to the page before it (older messages), for paging.</summary>
internal sealed record MessagePage(IReadOnlyList<TeamsMessage> Messages, string? OlderLink);

/// <summary>A person, for turning an mri into a name in the Markdown.</summary>
internal sealed record Person(string Mri, string DisplayName);
