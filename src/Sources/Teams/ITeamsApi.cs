namespace AnyFS.Sources.Teams;

/// <summary>
/// What a Teams source reads, from Microsoft's own Teams services (spec 7.9): the account's teams and channels and chats
/// (the aggregator), a conversation's messages (the chat service), and a person's name (the middle tier). The live
/// implementation signs in with the web session and calls those services with the tokens the Teams web client mints; the
/// fake stands in for them in tests. Files are read through <see cref="ITeamsFiles"/>, added with the files feature.
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
}
