using AnyFS.Sdk;

namespace AnyFS.Sources.Teams;

/// <summary>
/// The Teams services the source calls, each with a bearer of its own audience that the web client mints (spike 18). The
/// audiences differ by cloud — Microsoft 365: ic3.teams.office.com, chatsvcagg.teams.microsoft.com, api.spaces.skype.com;
/// GCC High: ic3.gov.teams.microsoft.us, chatsvcagg.gov.teams.microsoft.us, api.spaces.skype.com (a sign-in audit at a
/// GCC High tenant, 29-Oct-2026) — so a token is matched to its service by its audience's host, not by a fixed string.
/// </summary>
internal enum TeamsService
{
    /// <summary>The chat service: a conversation's messages (an audience host of ic3.*).</summary>
    ChatService,

    /// <summary>The aggregator: the teams, their channels and the chats (chatsvcagg.*).</summary>
    Aggregator,

    /// <summary>The middle tier: the region's service discovery and people's names (api.spaces.*).</summary>
    MiddleTier,

    /// <summary>
    /// SharePoint and OneDrive: a team's files, the files shared in chats, the group sites. Its audience is SharePoint's app
    /// id in both clouds (00000003-0000-0ff1-ce00-000000000000), or a tenant's SharePoint host. The page mints it as it gets
    /// round to it (within seconds in Microsoft 365, not always in a headless load in GCC High): kept when the page sends
    /// it, never waited for by a listing, asked for in the background when not in hand.
    /// </summary>
    SharePoint,

    /// <summary>
    /// Microsoft Graph (graph.microsoft.com, graph.microsoft.us, dod-graph.microsoft.us): GCC High's web client mints one
    /// (Group.Read.All among its scopes), Microsoft 365's none (spike 18). Kept when the page sends it, never waited for by a
    /// listing, asked for in the background in the US Gov clouds; <c>groups.md</c> lists every group of the organisation
    /// through it where there is one.
    /// </summary>
    Graph,
}

/// <summary>
/// Why a request was not made: the page has not handed out the token for its service (SharePoint's, Graph's), which no
/// request waits for. It is a refusal's inner exception, so that refusal does not count against the source's health.
/// </summary>
internal sealed class TokenNotHandedOutException(TeamsService service)
    : Exception($"Teams on the web has not handed out its {service} token yet; AnyFS asks for it in the background")
{
    /// <summary>The refusal a request gets for want of the token: unavailable for now.</summary>
    public static VfsException Refusal(TeamsService service)
    {
        var why = new TokenNotHandedOutException(service);
        return new VfsException(VfsError.Unavailable, why.Message, why);
    }

    /// <summary>Whether a refusal was for want of such a token, not the source's health.</summary>
    public static bool IsCause(VfsException ex) => ex.InnerException is TokenNotHandedOutException;
}

internal static class TeamsServices
{
    /// <summary>The services every sign-in reads before it is done: the teams, chats, messages and people.</summary>
    public static readonly TeamsService[] Core = [TeamsService.ChatService, TeamsService.Aggregator, TeamsService.MiddleTier];

    /// <summary>SharePoint's app id: the audience of a SharePoint or OneDrive token in every cloud seen.</summary>
    public const string SharePointApp = "00000003-0000-0ff1-ce00-000000000000";

    /// <summary>Microsoft Graph's app id, an audience a Graph token can carry instead of its host.</summary>
    public const string GraphApp = "00000003-0000-0000-c000-000000000000";

    /// <summary>The service a token's audience is for, by the audience's host; null for one the source does not use.</summary>
    public static TeamsService? Of(string? audience)
    {
        if (audience is not null && audience.Trim().Equals(SharePointApp, StringComparison.OrdinalIgnoreCase))
        {
            return TeamsService.SharePoint;
        }

        if (audience is not null && audience.Trim().Equals(GraphApp, StringComparison.OrdinalIgnoreCase))
        {
            return TeamsService.Graph;
        }

        if (audience is not null && Uri.TryCreate(audience.Trim(), UriKind.Absolute, out var graph) && IsGraphHost(graph.Host))
        {
            return TeamsService.Graph;
        }

        if (audience is null || !Uri.TryCreate(audience.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host;
        return host.StartsWith("ic3.", StringComparison.OrdinalIgnoreCase) ? TeamsService.ChatService
            : host.StartsWith("chatsvcagg.", StringComparison.OrdinalIgnoreCase) ? TeamsService.Aggregator
            : host.StartsWith("api.spaces.", StringComparison.OrdinalIgnoreCase) ? TeamsService.MiddleTier
            : IsSharePointHost(host) ? TeamsService.SharePoint
            : null;
    }

    /// <summary>Microsoft Graph's host in a cloud: graph.microsoft.com, graph.microsoft.us (GCC High), dod-graph.microsoft.us.</summary>
    public static bool IsGraphHost(string host)
        => host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase) || host.Equals("graph.microsoft.us", StringComparison.OrdinalIgnoreCase)
            || host.Equals("dod-graph.microsoft.us", StringComparison.OrdinalIgnoreCase);

    /// <summary>A tenant's SharePoint or OneDrive host, in any cloud (sharepoint.com, sharepoint.us, sharepoint-mil.us, sharepoint.cn).</summary>
    public static bool IsSharePointHost(string host)
        => host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".sharepoint.us", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".sharepoint-mil.us", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".sharepoint.cn", StringComparison.OrdinalIgnoreCase);
}
