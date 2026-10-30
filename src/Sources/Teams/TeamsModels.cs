namespace AnyFS.Sources.Teams;

/// <summary>A team the account is in: the aggregator's <c>teams[]</c> entry (spec 7.9, spike 18).</summary>
/// <param name="Id">The team's thread id (<c>19:…@thread.tacv2</c>).</param>
/// <param name="GroupId">The Microsoft 365 group behind the team, from which its SharePoint site is found (files).</param>
internal sealed record TeamInfo(string Id, string DisplayName, string? GroupId, IReadOnlyList<ChannelInfo> Channels)
{
    /// <summary>Whether the account owns the team (the aggregator's <c>memberRoleString</c> "admin"), not only belongs to it.</summary>
    public bool Owner { get; init; }

    /// <summary>Who may join: the aggregator's <c>accessType</c> (1 private, 2 public, 3 org-wide); 0 when it does not say.</summary>
    public int AccessType { get; init; }

    /// <summary>How many belong to it (<c>membershipSummary.totalMemberCount</c>); null when not given.</summary>
    public int? Members { get; init; }
}

/// <summary>A Microsoft 365 group's SharePoint site, as search finds those the account can open.</summary>
/// <param name="GroupId">The group's id where the search names it (SharePoint's does; Microsoft Search through Graph does not).</param>
internal sealed record GroupSite(string? GroupId, string Name, string SiteUrl, string? Description, DateTimeOffset? Created);

/// <summary>A group of the organisation and what is known of it: from Microsoft Graph everything there; else what SharePoint and the aggregator tell.</summary>
internal sealed record GroupInfo(string Id, string Name)
{
    /// <summary>Microsoft 365 group, Security group, Mail-enabled security group or Distribution list.</summary>
    public string? Kind { get; init; }

    /// <summary>Public, Private, Hidden membership or Org-wide; null where unknown or for a group that has none.</summary>
    public string? Visibility { get; init; }

    /// <summary>Whether it has a team; null where unknown.</summary>
    public bool? Team { get; init; }

    public bool Dynamic { get; init; }

    public string? Description { get; init; }

    public string? Mail { get; init; }

    public string? Alias { get; init; }

    public string? Classification { get; init; }

    public string? MembershipRule { get; init; }

    public string? SiteUrl { get; init; }

    public DateTimeOffset? Created { get; init; }

    public DateTimeOffset? Renewed { get; init; }

    public DateTimeOffset? Expires { get; init; }

    public bool? OnPremisesSynced { get; init; }

    public bool? RoleAssignable { get; init; }

    public int? Members { get; init; }

    /// <summary>A team's group as the aggregator tells of it: a Microsoft 365 group with a team, its visibility from who may join, its member count.</summary>
    public static GroupInfo OfTeam(TeamInfo team) => new(team.GroupId ?? team.Id, team.DisplayName)
    {
        Kind = "Microsoft 365 group",
        Visibility = team.AccessType switch { 1 => "Private", 2 => "Public", 3 => "Org-wide", _ => null },
        Team = true,
        Members = team.Members,
    };
}

/// <summary>
/// The groups for <c>groups.md</c>: every one in the organisation (<see cref="Complete"/>, from Microsoft Graph), else those
/// the account can see, else (<see cref="JoinedOnly"/>) those of its own teams; with the ones it belongs to and owns, and
/// where they came from.
/// </summary>
internal sealed record GroupsDirectory(IReadOnlyList<GroupInfo> Groups, IReadOnlySet<string> Member, IReadOnlySet<string> Owner, bool Complete, string From)
{
    /// <summary>Only the account's own teams' groups: Teams on the web had handed out no token that shows others (SharePoint's, Graph's).</summary>
    public bool JoinedOnly { get; init; }

    /// <summary>The groups of the account's own teams, as the aggregator tells of them: all there is to go on without SharePoint's token or Graph's.</summary>
    public static GroupsDirectory OfTeams(IReadOnlyList<TeamInfo> joined) => new(
        joined.Where(t => t.GroupId is not null).Select(GroupInfo.OfTeam).ToList(),
        joined.Select(t => t.GroupId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase),
        joined.Where(t => t.Owner).Select(t => t.GroupId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase),
        Complete: false,
        "Teams")
    {
        JoinedOnly = true,
    };
}

/// <summary>A channel of a team: its own conversation thread.</summary>
/// <param name="Id">The channel's thread id, which is also the conversation id its messages are read by.</param>
/// <param name="IsPrivate">A private (or shared) channel, whose files live in a site of its own.</param>
internal sealed record ChannelInfo(string Id, string DisplayName, bool IsGeneral, bool IsPrivate)
{
    /// <summary>
    /// The server-relative path of the channel's folder (<c>defaultFileSettings.filesRelativePath</c>), where Teams gives it
    /// (a private channel's points into its own site); often null, when the folder is the channel's name in the team's library.
    /// </summary>
    public string? FilesPath { get; init; }

    /// <summary>The aggregator's version of the channel's conversation, which moves on as it changes: an unchanged one is not read again.</summary>
    public string? Version { get; init; }
}

/// <summary>A team's SharePoint site and the server-relative path of its default library (<c>/sites/x/Shared Documents</c>).</summary>
internal sealed record TeamSite(string Url, string LibraryPath);

/// <summary>A file a message shares (<c>properties.files</c>): its name and address in SharePoint or OneDrive.</summary>
/// <param name="SiteUrl">The site it is in, where the message says (<c>fileInfo.siteUrl</c>, <c>baseUrl</c>); else taken from its address.</param>
internal sealed record SharedFile(string Name, string Url, string? SiteUrl);

/// <summary>A folder or file of a site's library, as SharePoint's v2.0 drive API lists it.</summary>
/// <param name="DownloadUrl">A file's pre-authenticated download address (about an hour's life), which takes ranges; null for a folder.</param>
internal sealed record DriveEntry(string Name, bool IsFolder, long Length, DateTimeOffset? Modified, string? DownloadUrl);

/// <summary>An inline image's bytes and its file extension (from its content), as Teams' media service serves it.</summary>
internal sealed record TeamsImage(byte[] Bytes, string Extension);

internal enum ChatKind
{
    OneOnOne,
    Group,
    Meeting,
}

/// <summary>A chat the account is in: the aggregator's <c>chats[]</c> entry.</summary>
/// <param name="Title">The chat's own topic, where it has one; null for a 1:1 or an untitled group (then named for its members).</param>
/// <param name="MemberMris">The other people's mris (the account's own left out), for naming a 1:1 or group chat.</param>
internal sealed record ChatInfo(string Id, string? Title, ChatKind Kind, IReadOnlyList<string> MemberMris, DateTimeOffset? LastUpdated)
{
    /// <summary>The aggregator's version of the chat, which moves on as it changes: an unchanged one is not read again.</summary>
    public string? Version { get; init; }
}

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

    /// <summary>The files it shares (<c>properties.files</c>); empty when none.</summary>
    public IReadOnlyList<SharedFile> Files { get; init; } = [];

    /// <summary>The addresses of its inline images (its HTML's AMSImage <c>img</c> elements), in order; empty when none.</summary>
    public IReadOnlyList<string> Images { get; init; } = [];

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
