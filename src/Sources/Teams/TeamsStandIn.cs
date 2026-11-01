namespace AnyFS.Sources.Teams;

/// <summary>
/// The seam that gives a source its <see cref="ITeamsApi"/> in tests: the fake's, in place of the live web session; with
/// how far back a conversation's newest read goes and how many messages a page holds, so a test's few messages have a
/// history.
/// </summary>
internal sealed record TeamsStandIn(ITeamsApi Api, int NewestMessages = 2_000, int PageSize = 200);
