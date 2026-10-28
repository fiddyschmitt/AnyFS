using System.Globalization;
using System.Text;
using ReverseMarkdown;

namespace AnyFS.Sources.Teams;

/// <summary>
/// A conversation as Markdown (spec 7.9): a channel post (the opening message and its replies) or a whole chat, oldest
/// first, each message headed by who sent it and when, its HTML body converted as the SharePoint pages and Outlook bodies
/// are (ReverseMarkdown). System activities (a member added, a call, a rename) are left out. UTF-8, no byte-order mark.
/// </summary>
internal static class TeamsText
{
    private static readonly Lock ConverterGate = new();
    private static readonly Converter Markdown = new(new Config
    {
        Tags = { Unknown = Config.UnknownTagsOption.Bypass },
        Links = { SmartHref = true },
        Formatting = { RemoveComments = true, ListBulletChar = '-', OutputLineEnding = "\n" },
    });

    /// <summary>A channel post: the thread's messages (the root first, then its replies in time order), with a heading.</summary>
    public static byte[] Post(IReadOnlyList<TeamsMessage> thread, Func<string?, string> nameOf)
    {
        var text = new StringBuilder();
        var root = thread[0];
        text.Append("# ").Append(Clean(Subject(root, nameOf))).Append("\n\n");
        Conversation(text, thread, nameOf);
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>A chat: all of its messages, oldest first.</summary>
    public static byte[] Chat(IReadOnlyList<TeamsMessage> messages, string title, Func<string?, string> nameOf)
    {
        var text = new StringBuilder();
        text.Append("# ").Append(Clean(title)).Append("\n\n");
        Conversation(text, messages, nameOf);
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static void Conversation(StringBuilder text, IReadOnlyList<TeamsMessage> messages, Func<string?, string> nameOf)
    {
        var any = false;
        foreach (var message in messages.Where(m => m.IsContent).OrderBy(m => m.Composed ?? DateTimeOffset.MinValue).ThenBy(m => m.Id, StringComparer.Ordinal))
        {
            any = true;
            var who = message.FromDisplayName is { Length: > 0 } ? message.FromDisplayName : nameOf(message.FromMri);
            text.Append("**").Append(Clean(who)).Append("**");
            if (message.Composed is { } when)
            {
                text.Append("  ·  ").Append(when.ToLocalTime().ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture));
            }

            text.Append("\n\n").Append(Body(message)).Append("\n\n");
        }

        if (!any)
        {
            text.Append("*No messages.*\n");
        }
    }

    /// <summary>A message's body as Markdown: the HTML converted, or the plain text; "" when empty.</summary>
    private static string Body(TeamsMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Html))
        {
            return "";
        }

        if (message.MessageType.Equals("Text", StringComparison.OrdinalIgnoreCase))
        {
            return message.Html.Trim();
        }

        string markdown;
        lock (ConverterGate)
        {
            markdown = Markdown.Convert(message.Html);
        }

        return markdown.Trim();
    }

    /// <summary>A post's heading: the first line of the opening message, else who opened it and when.</summary>
    private static string Subject(TeamsMessage root, Func<string?, string> nameOf)
    {
        var body = Body(root).Replace("\r", "", StringComparison.Ordinal);
        var firstLine = body.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().TrimStart('#', '*', '>', ' ');
        if (!string.IsNullOrWhiteSpace(firstLine))
        {
            return firstLine.Length <= 80 ? firstLine : firstLine[..80].TrimEnd() + "…";
        }

        var who = root.FromDisplayName is { Length: > 0 } ? root.FromDisplayName : nameOf(root.FromMri);
        return root.Composed is { } when ? $"{who} · {when.ToLocalTime():d MMM yyyy HH:mm}" : who;
    }

    /// <summary>The thread-folder name: the time, the author and the subject, safe for Windows.</summary>
    public static string ThreadName(TeamsMessage root, Func<string?, string> nameOf)
    {
        var who = root.FromDisplayName is { Length: > 0 } ? root.FromDisplayName : nameOf(root.FromMri);
        var when = root.Composed?.ToLocalTime().ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture);
        var parts = new[] { when, who, Subject(root, nameOf) }.Where(p => !string.IsNullOrWhiteSpace(p));
        return TeamsNames.Safe(string.Join(" ", parts));
    }

    private static string Clean(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
