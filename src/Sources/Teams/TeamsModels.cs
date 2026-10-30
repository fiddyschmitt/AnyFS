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

    /// <summary>How many of them own it (<c>membershipSummary.adminRoleCount</c>); null when not given.</summary>
    public int? Owners { get; init; }

    /// <summary>How many bots are in it (<c>membershipSummary.botCount</c>); null when not given.</summary>
    public int? Bots { get; init; }

    public string? Description { get; init; }

    /// <summary>The team's email address (<c>smtpAddress</c>).</summary>
    public string? Mail { get; init; }

    public string? Classification { get; init; }

    /// <summary>Shown among the account's favourites in Teams (<c>isFavorite</c>).</summary>
    public bool Favourite { get; init; }

    public bool Archived { get; init; }

    /// <summary>Locked by its owners (<c>isTeamLocked</c>).</summary>
    public bool Locked { get; init; }
}

/// <summary>A tab of a channel (a website, an app, a pinned file): its name, what kind it is, and a website's address.</summary>
internal sealed record TabInfo(string Name, string Kind, string? Url);

/// <summary>A Microsoft 365 group whose SharePoint site the account can open, as a search or Graph's list of groups finds it.</summary>
/// <param name="GroupId">The group's id where it is named (SharePoint's search and Graph's list do; Microsoft Search through Graph does not).</param>
/// <param name="SiteUrl">The site's address where it is named (the searches do; Graph's list of groups does not, so it is found when the group is opened).</param>
internal sealed record GroupSite(string? GroupId, string Name, string? SiteUrl, string? Description, DateTimeOffset? Created);

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

/// <summary>What a list of groups covers.</summary>
internal enum GroupsScope
{
    /// <summary>Every group in the organisation (Microsoft Graph's list of groups): <c>all-groups.md</c>.</summary>
    Organisation,

    /// <summary>The groups the account can see (SharePoint's search: every public group and the private ones it is in): <c>all-groups.md</c> without Graph.</summary>
    Visible,

    /// <summary>The groups the account is in, of every kind (Graph's list of its memberships): <c>groups.md</c>.</summary>
    Yours,

    /// <summary>The groups of the account's own teams (the aggregator): <c>groups.md</c> without Graph.</summary>
    Teams,
}

/// <summary>A list of groups for <c>groups.md</c> or <c>all-groups.md</c>: what it covers, the ones the account belongs to and owns, and where they came from.</summary>
internal sealed record GroupsDirectory(IReadOnlyList<GroupInfo> Groups, IReadOnlySet<string> Member, IReadOnlySet<string> Owner, GroupsScope Scope, string From)
{
    /// <summary>The groups of the account's own teams, as the aggregator tells of them: all there is to go on without Graph's token.</summary>
    public static GroupsDirectory OfTeams(IReadOnlyList<TeamInfo> joined) => new(
        joined.Where(t => t.GroupId is not null).Select(GroupInfo.OfTeam).ToList(),
        joined.Select(t => t.GroupId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase),
        joined.Where(t => t.Owner).Select(t => t.GroupId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase),
        GroupsScope.Teams,
        "Teams");
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

    public string? Description { get; init; }

    /// <summary>When it was made (<c>creationTime</c>), and by whom (<c>creator</c>, an mri).</summary>
    public DateTimeOffset? Created { get; init; }

    public string? CreatorMri { get; init; }

    /// <summary>A shared channel (<c>isShared</c>): one shared with other teams or people of other organisations.</summary>
    public bool Shared { get; init; }

    /// <summary>Shown among the account's favourites in Teams (<c>isFavorite</c>).</summary>
    public bool Favourite { get; init; }

    public IReadOnlyList<TabInfo> Tabs { get; init; } = [];
}

/// <summary>
/// What the chat service tells of a channel's thread beyond the aggregator: where its files are and how many are in it
/// (<c>/v1/threads/{id}</c>: its properties' SharePoint site and folder, its roster summary).
/// </summary>
internal sealed record ThreadDetails(string? SiteUrl, string? FolderPath, int? Members, int? Owners, int? External, int? Bots);

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

    /// <summary>Hidden from the chat list in Teams (<c>hidden</c>); a new message shows it again.</summary>
    public bool Hidden { get; init; }

    /// <summary>When it was started (<c>createdAt</c>), and by whom (<c>creator</c>, an mri).</summary>
    public DateTimeOffset? Created { get; init; }

    public string? CreatorMri { get; init; }

    /// <summary>With people of other organisations (<c>isExternal</c>).</summary>
    public bool External { get; init; }

    /// <summary>Muted by the account (its own member entry's <c>isMuted</c>).</summary>
    public bool Muted { get; init; }

    /// <summary>The ids of the messages pinned in it (<c>pinnedItems</c>, where it names them).</summary>
    public IReadOnlyList<string> PinnedIds { get; init; } = [];

    /// <summary>A meeting's chat's meeting, as the chat names it; null for another chat.</summary>
    public MeetingInfo? Meeting { get; init; }
}

/// <summary>A meeting as its chat names it (the aggregator's <c>meetingInformation</c>), with the account's answer to it.</summary>
/// <param name="Start">When it is scheduled for (a recurring meeting's: as Teams gives it), and <paramref name="End"/>.</param>
/// <param name="OrganizerId">The organiser, by object id (or mri).</param>
/// <param name="Recurrence">How it repeats, in words (its pattern and range as Teams gives them).</param>
/// <param name="Kind">Teams' kind of meeting (<c>meetingType</c>, else <c>appointmentType</c>).</param>
/// <param name="Response">The account's answer (<c>userMeetingInfo.rsvpStatus</c>).</param>
internal sealed record MeetingInfo(string? Subject, string? Location, DateTimeOffset? Start, DateTimeOffset? End, bool Cancelled, string? JoinUrl,
    string? OrganizerId, string? Recurrence, string? Kind, string? Response);

/// <summary>A call in the account's call history (the chat service's <c>48:calllogs</c>, each message's <c>call-log</c>).</summary>
/// <param name="Direction">incoming or outgoing, as Teams writes it.</param>
/// <param name="State">How it went: accepted, missed, declined… as Teams writes it.</param>
/// <param name="Originator">Who called (a name), and <paramref name="Target"/> who was called.</param>
internal sealed record CallRecord(DateTimeOffset? Start, DateTimeOffset? Connected, DateTimeOffset? End, string? Direction, string? Type, string? State, string? Originator, string? Target, int Participants);

/// <summary>An entry of the account's activity feed (the chat service's <c>48:notifications</c> and <c>48:mentions</c>, each message's <c>activity</c>).</summary>
/// <param name="Type">What happened, as Teams names it (mentions, reply, reaction, teamMembershipChange…), with <paramref name="Subtype"/>.</param>
/// <param name="From">Who did it (a name, where Teams gives one).</param>
/// <param name="Topic">Where, as Teams names it (a thread's topic), where it does; else found from <paramref name="ThreadId"/>.</param>
/// <param name="Preview">The start of the message, as Teams shows it in the feed.</param>
/// <param name="Mention">A mention of the account (<c>48:mentions</c>, or a mention in the feed), each once.</param>
internal sealed record ActivityItem(DateTimeOffset? When, string Type, string? Subtype, string? From, string? Topic, string? ThreadId, string? Preview, bool Mention);

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

    /// <summary>Pinned in its conversation (<c>properties.pinned</c>).</summary>
    public bool Pinned { get; init; }

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

/// <summary>A person, for turning an mri into a name in the Markdown, and telling a guest, a bot or another organisation's person in chat.md.</summary>
internal sealed record Person(string Mri, string DisplayName)
{
    /// <summary>A guest in the organisation (the middle tier's <c>userType</c> Guest).</summary>
    public bool Guest { get; init; }

    /// <summary>A bot or an app (<c>type</c> BOT).</summary>
    public bool Bot { get; init; }

    /// <summary>Their organisation's name (<c>tenantName</c>).</summary>
    public string? Organisation { get; init; }
}

/// <summary>What chat.md says of its chat above the messages (spec 7.9).</summary>
/// <param name="Link">The address that opens the chat in Teams.</param>
/// <param name="Members">The members by name, the account as "you", each guest, bot or another organisation's person marked; at most <see cref="TeamsText.MembersShown"/> of <paramref name="MemberCount"/>.</param>
/// <param name="StartedBy">Who started it, by name.</param>
/// <param name="Pinned">Its pinned messages among those read.</param>
/// <param name="Earlier">Where its earlier messages are, where it goes back further than chat.md.</param>
internal sealed record ChatDetails(string? Link, ChatKind Kind, IReadOnlyList<string> Members, int MemberCount, DateTimeOffset? Started, string? StartedBy,
    bool External, bool Muted, bool Hidden, IReadOnlyList<TeamsMessage> Pinned, string? Earlier = null);

/// <summary>A calendar month in local time: a conversation's earlier messages are in a folder per month (<c>2024-03</c>).</summary>
internal readonly record struct Month(int Year, int Number)
{
    public string Name => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Year:D4}-{Number:D2}");

    /// <summary>Its first moment, in local time.</summary>
    public DateTimeOffset Start => new(new DateTime(Year, Number, 1, 0, 0, 0, DateTimeKind.Local));

    /// <summary>In words: "March 2024".</summary>
    public string Label => new DateTime(Year, Number, 1).ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    public static Month Of(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        return new Month(local.Year, local.Month);
    }

    /// <summary>A month by its folder's name (<c>2024-03</c>).</summary>
    public static bool TryParse(string name, out Month month)
    {
        month = default;
        if (name.Length != 7 || name[4] != '-' || !int.TryParse(name.AsSpan(0, 4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(name.AsSpan(5, 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) || year < 1 || number is < 1 or > 12)
        {
            return false;
        }

        month = new Month(year, number);
        return true;
    }
}

/// <summary>What a chat's folder holds: chat.md, its attachments, whether it has earlier messages, its meeting.md (a meeting chat's) and its recordings.</summary>
internal sealed record ChatParts(byte[] Markdown, IReadOnlyList<TmNode> Attachments, bool Earlier, byte[]? Meeting, IReadOnlyList<TmNode> Recordings);
