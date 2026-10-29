namespace AnyFS.Sources.Teams;

/// <summary>
/// What a Teams source reads, from Microsoft's own Teams services (spec 7.9): the account's teams and channels and chats
/// (the aggregator), a conversation's messages (the chat service), and a person's name (the middle tier). The live
/// implementation signs in with the web session and calls those services with the tokens the Teams web client mints; the
/// fake stands in for them in tests. Files are read from SharePoint and OneDrive, and inline images from Teams' media
/// service, with the tokens the same client mints for them.
/// </summary>
internal interface ITeamsApi : IAsyncDisposable
{
    /// <summary>The account signed in as, from the token once signed in (for messages and naming the "me" side of a chat).</summary>
    string Account { get; }

    /// <summary>The account's own mri (<c>8:orgid:&lt;object id&gt;</c>), so its own messages and chat membership are known.</summary>
    string? Mri { get; }

    /// <summary>The teams the account is in (each with its channels) and its chats (1:1, group and meeting), from one call: the aggregator answers both at once.</summary>
    Task<TeamsStructure> StructureAsync(CancellationToken ct);

    /// <summary>A conversation's newest messages (a channel's or a chat's, by its thread id), up to <paramref name="max"/>, with a link to older ones.</summary>
    Task<MessagePage> MessagesAsync(string threadId, int max, CancellationToken ct);

    /// <summary>The next page back (older messages) from a page's <see cref="MessagePage.OlderLink"/>.</summary>
    Task<MessagePage> OlderAsync(string olderLink, CancellationToken ct);

    /// <summary>The people's display names by mri, as many as are known in one call (the middle tier); an mri with no name is left out.</summary>
    Task<IReadOnlyList<Person>> PeopleAsync(IReadOnlyList<string> mris, CancellationToken ct);

    /// <summary>
    /// Where a channel's files are: its site and the folder's path within the site's default library (the channel's
    /// <see cref="ChannelInfo.FilesPath"/> where Teams gives one, else the channel's name in its team's site, found from the
    /// team's Microsoft 365 group); null when the team has no site (yet).
    /// </summary>
    Task<(string SiteUrl, string Path)?> ChannelFilesAsync(TeamInfo team, ChannelInfo channel, CancellationToken ct);

    /// <summary>A folder of a site's default library, by its path within the library (<c>General/Specs</c>): its folders and files; empty when it does not exist.</summary>
    Task<IReadOnlyList<DriveEntry>> FolderAsync(string siteUrl, string path, CancellationToken ct);

    /// <summary>A file of a site's default library, by its path within the library, with a fresh download address; null when it is gone.</summary>
    Task<DriveEntry?> FileAsync(string siteUrl, string path, CancellationToken ct);

    /// <summary>A file's content from an offset, by its download address (a range where SharePoint takes one).</summary>
    Task<HttpResponseMessage> ContentAsync(string downloadUrl, long offset, CancellationToken ct);

    /// <summary>An inline image's bytes, from Teams' media service; null when it cannot be read (a host other than Teams' own is never asked).</summary>
    Task<TeamsImage?> ImageAsync(string src, CancellationToken ct);

    /// <summary>
    /// Whether SharePoint's token (and the tenant's host) is in hand now: what the files, the attachments and Other groups
    /// are read with. Never a wait: the page mints that token only as it gets round to it, and no listing waits on it.
    /// </summary>
    bool SharePointReady { get; }

    /// <summary>Whether a Microsoft Graph token is in hand now (GCC High's client mints one, Microsoft 365's never): every group of the organisation is listed with it.</summary>
    bool GraphReady { get; }

    /// <summary>
    /// For the background alone (nothing waits on it): asks the page for the tokens it has not handed out — SharePoint's,
    /// and Graph's where the cloud's client mints one — in one headless load at most, backed off when none comes; true
    /// when one came.
    /// </summary>
    Task<bool> SeekTokensAsync(CancellationToken ct);

    /// <summary>Every Microsoft 365 group site the account can open (SharePoint search, which leaves out what it may not see).</summary>
    Task<IReadOnlyList<GroupSite>> GroupSitesAsync(CancellationToken ct);

    /// <summary>
    /// Every group there is, with its details, from the tokens in hand (never waiting for one): every group of the
    /// organisation from Microsoft Graph where there is a Graph token; else <paramref name="sites"/>, the group sites the
    /// account can open, with SharePoint's details of each; else (no sites: SharePoint's token not in hand) the account's
    /// own teams; with what the aggregator says of <paramref name="joined"/>.
    /// </summary>
    Task<GroupsDirectory> GroupsAsync(IReadOnlyList<TeamInfo> joined, IReadOnlyList<GroupSite>? sites, CancellationToken ct);
}
