namespace AnyFS.Sources.Teams;

/// <summary>
/// The addresses that open a team, a channel, a post or a chat in Teams (spec 7.9): Microsoft's deep links
/// (learn.microsoft.com, "Deep links in Teams") on the cloud's own Teams host, the first line of team.md, channel.md,
/// post.md and chat.md, as a SharePoint page's Markdown starts with the page's address.
/// </summary>
internal static class TeamsLinks
{
    public static string Team(string host, TeamInfo team) => $"{host}/l/team/{E(team.Id)}/conversations{Group(team)}";

    public static string Channel(string host, TeamInfo team, ChannelInfo channel) => $"{host}/l/channel/{E(channel.Id)}/{E(channel.DisplayName)}{Group(team)}";

    /// <summary>A channel post, by its opening message (its thread's id, though only replies were read), with the time it was posted where that is known.</summary>
    public static string Post(string host, TeamInfo team, ChannelInfo channel, IReadOnlyList<TeamsMessage> thread)
    {
        var root = thread[0].ThreadId;
        var posted = thread.FirstOrDefault(m => m.Id == root)?.Composed;
        return $"{host}/l/message/{E(channel.Id)}/{E(root)}?{(team.GroupId is { } group ? $"groupId={E(group)}&" : "")}parentMessageId={E(root)}"
            + (posted is { } at ? $"&createdTime={at.ToUnixTimeMilliseconds()}" : "");
    }

    public static string Chat(string host, ChatInfo chat) => $"{host}/l/chat/{E(chat.Id)}/conversations";

    private static string Group(TeamInfo team) => team.GroupId is { } group ? $"?groupId={E(group)}" : "";

    private static string E(string value) => Uri.EscapeDataString(value);
}
