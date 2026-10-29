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
}

internal static class TeamsServices
{
    public static readonly TeamsService[] All = Enum.GetValues<TeamsService>();

    /// <summary>The service a token's audience is for, by the audience's host; null for one the source does not use.</summary>
    public static TeamsService? Of(string? audience)
    {
        if (audience is null || !Uri.TryCreate(audience.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host;
        return host.StartsWith("ic3.", StringComparison.OrdinalIgnoreCase) ? TeamsService.ChatService
            : host.StartsWith("chatsvcagg.", StringComparison.OrdinalIgnoreCase) ? TeamsService.Aggregator
            : host.StartsWith("api.spaces.", StringComparison.OrdinalIgnoreCase) ? TeamsService.MiddleTier
            : null;
    }
}
