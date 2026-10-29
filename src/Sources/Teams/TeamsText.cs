using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using ReverseMarkdown;

namespace AnyFS.Sources.Teams;

/// <summary>
/// A conversation as Markdown (spec 7.9): a channel post (the opening message and its replies) or a whole chat, oldest
/// first, each message headed by who sent it and when, its HTML body converted as the SharePoint pages and Outlook bodies
/// are (ReverseMarkdown). System activities (a member added, a call, a rename) are left out. UTF-8, no byte-order mark.
/// A message is converted once however often its thread is named or built (<see cref="Converted"/>).
/// </summary>
/// <summary>A conversation's attachments' names in its attachments folder: an inline image's by its address, a shared file's by its.</summary>
internal sealed record AttachmentNames(IReadOnlyDictionary<string, string> Images, IReadOnlyDictionary<string, string> Files)
{
    public static readonly AttachmentNames None = new(new Dictionary<string, string>(), new Dictionary<string, string>());
}

internal static class TeamsText
{
    private static readonly Lock ConverterGate = new();
    private static readonly Converter Markdown = new(new Config
    {
        Tags = { Unknown = Config.UnknownTagsOption.Bypass },
        Links = { SmartHref = true },
        Formatting = { RemoveComments = true, ListBulletChar = '-', OutputLineEnding = "\n" },
    });

    // a message's Markdown body, by the message itself: a fetched page's messages are reused for every naming and build
    // until the page is fetched again, so each is converted once (a posts folder named and built its roots three times)
    private static readonly ConditionalWeakTable<TeamsMessage, string> Converted = new();

    /// <summary>A channel post: the thread's messages (the root first, then its replies in time order), under a heading.</summary>
    public static byte[] Post(IReadOnlyList<TeamsMessage> thread, Func<string?, string> nameOf, AttachmentNames? attachments = null)
    {
        var text = new StringBuilder();
        var root = thread[0];
        var heading = Subject(root) ?? $"{Who(root, nameOf)} · {Stamp(root.Composed, "d MMM yyyy HH:mm")}";
        text.Append("# ").Append(Clean(heading)).Append("\n\n");
        Conversation(text, thread, nameOf, attachments ?? AttachmentNames.None);
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>A chat: all of its messages, oldest first.</summary>
    public static byte[] Chat(IReadOnlyList<TeamsMessage> messages, string title, Func<string?, string> nameOf, AttachmentNames? attachments = null)
    {
        var text = new StringBuilder();
        text.Append("# ").Append(Clean(title)).Append("\n\n");
        Conversation(text, messages, nameOf, attachments ?? AttachmentNames.None);
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>The thread-folder name: the time, the author and, where the opening message has text, its first line; safe for Windows.</summary>
    public static string ThreadName(TeamsMessage root, Func<string?, string> nameOf)
    {
        var parts = new[] { Stamp(root.Composed, "yyyy-MM-dd HHmm"), Who(root, nameOf), Subject(root) }.Where(p => !string.IsNullOrWhiteSpace(p));
        return TeamsNames.Safe(string.Join(" ", parts));
    }

    private static void Conversation(StringBuilder text, IReadOnlyList<TeamsMessage> messages, Func<string?, string> nameOf, AttachmentNames attachments)
    {
        var any = false;
        foreach (var message in messages.Where(m => m.IsContent).OrderBy(m => m.Composed ?? DateTimeOffset.MinValue).ThenBy(m => m.Id, StringComparer.Ordinal))
        {
            any = true;
            text.Append("**").Append(Clean(Who(message, nameOf))).Append("**");
            if (message.Composed is not null)
            {
                text.Append("  ·  ").Append(Stamp(message.Composed, "ddd d MMM yyyy HH:mm"));
            }

            if (message.Edited is not null && message.Deleted is null)
            {
                text.Append("  ·  edited");
            }

            // a deleted message keeps its place in the conversation, as Teams shows it
            text.Append("\n\n").Append(message.Deleted is null ? Linked(message, attachments) : "*This message was deleted.*").Append("\n\n");
            if (message.Deleted is null && message.Files.Count > 0)
            {
                // a shared file, linked to its copy in the attachments folder where it is there
                foreach (var file in message.Files)
                {
                    text.Append("- 📎 ").Append(attachments.Files.TryGetValue(file.Url, out var name) ? $"[{Escape(file.Name)}](<attachments/{name}>)" : Escape(file.Name)).Append('\n');
                }

                text.Append('\n');
            }

            if (message.Deleted is null && message.Reactions.Count > 0)
            {
                text.Append(string.Join("  ·  ", message.Reactions.Select(r => $"{EmojiOf(r.Key)} {string.Join(", ", r.Mris.Select(nameOf).Distinct())}"))).Append("\n\n");
            }
        }

        if (!any)
        {
            text.Append("*No messages.*\n");
        }
    }

    // Teams' six classic reactions; the rest name their emoji by its code point (1f600_grinningface)
    private static readonly Dictionary<string, string> Emoji = new(StringComparer.OrdinalIgnoreCase)
    {
        ["like"] = "👍",
        ["heart"] = "❤️",
        ["laugh"] = "😆",
        ["surprised"] = "😮",
        ["sad"] = "😢",
        ["angry"] = "😠",
    };

    /// <summary>A reaction's emoji: the classic six by name, another by the code point its key starts with, else <c>:key:</c>.</summary>
    internal static string EmojiOf(string key)
    {
        if (Emoji.TryGetValue(key, out var emoji))
        {
            return emoji;
        }

        var hex = key.Split('_', 2)[0];
        return hex.Length is >= 4 and <= 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint)
            && codePoint is >= 0x2000 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF)
            ? char.ConvertFromUtf32(codePoint)
            : $":{key}:";
    }

    private static string Who(TeamsMessage message, Func<string?, string> nameOf)
        => message.FromDisplayName is { Length: > 0 } name ? name : nameOf(message.FromMri);

    /// <summary>A local time in a fixed, culture-free form, so a folder name is the same on every machine.</summary>
    private static string? Stamp(DateTimeOffset? when, string format) => when?.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);

    /// <summary>A message's body as Markdown: the HTML converted, or the plain text; "" when empty. Converted once per message.</summary>
    private static string Body(TeamsMessage message) => Converted.GetValue(message, Convert);

    /// <summary>The body with each inline image read into the attachments folder pointing at its copy there.</summary>
    private static string Linked(TeamsMessage message, AttachmentNames attachments)
    {
        var body = Body(message);
        foreach (var src in message.Images)
        {
            if (attachments.Images.TryGetValue(src, out var name))
            {
                // a twin's name ("… (2).png") has a space: such a link is written in angle brackets
                body = body.Replace(src, name.Contains(' ', StringComparison.Ordinal) ? $"<attachments/{name}>" : $"attachments/{name}", StringComparison.Ordinal);
            }
        }

        return body;
    }

    /// <summary>A file name as link text: the Markdown characters that would end or break it escaped.</summary>
    private static string Escape(string name) => name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);

    private static string Convert(TeamsMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Html))
        {
            return "";
        }

        if (message.MessageType.Equals("Text", StringComparison.OrdinalIgnoreCase))
        {
            return message.Html.Trim();
        }

        lock (ConverterGate)
        {
            return Markdown.Convert(message.Html).Trim();
        }
    }

    /// <summary>The first line of the opening message's text, cut at 80; null when it has none (an image-only post).</summary>
    private static string? Subject(TeamsMessage root)
    {
        var firstLine = Body(root).Replace("\r", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().TrimStart('#', '*', '>', ' ')).FirstOrDefault(l => l.Length > 0);
        return firstLine is null ? null : firstLine.Length <= 80 ? firstLine : firstLine[..80].TrimEnd() + "…";
    }

    private static string Clean(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
