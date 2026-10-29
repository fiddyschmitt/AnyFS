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

    /// <summary>
    /// <c>groups.md</c>: what the groups are and where they came from, a table of them all (name, kind, visibility, team, the
    /// account's part, email), then each one's details, every field known. Sorted by name.
    /// </summary>
    public static byte[] Groups(GroupsDirectory directory, string account)
    {
        var groups = directory.Groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id, StringComparer.Ordinal).ToList();
        string You(GroupInfo g) => directory.Owner.Contains(g.Id) ? "owner" : directory.Member.Contains(g.Id) ? "member" : "";
        string YesNo(bool? b) => b switch { true => "yes", false => "no", _ => "" };

        var text = new StringBuilder("# Groups\n\n");
        var mine = groups.Count(g => You(g).Length > 0);
        if (directory.Complete)
        {
            var kinds = groups.GroupBy(g => g.Kind ?? "Group").OrderByDescending(k => k.Count()).Select(k => $"{k.Count():N0} {Plural(k.Key, k.Count())}");
            var teams = groups.Count(g => g.Team == true);
            text.Append(CultureInfo.InvariantCulture, $"Every group in the organisation, as {directory.From} lists them for {account}: {groups.Count:N0} ({string.Join(", ", kinds)}; {teams:N0} with a team). ")
                .Append(CultureInfo.InvariantCulture, $"{account} is in {mine:N0} of them.\n\n");
        }
        else if (directory.JoinedOnly)
        {
            text.Append(CultureInfo.InvariantCulture, $"The groups of {account}'s own teams, from {directory.From}: {groups.Count:N0}. ")
                .Append("Teams on the web has not handed out the token that shows the others yet (SharePoint's, or Microsoft Graph's where it gives one); AnyFS asks for it in the background, and lists them here once it comes.\n\n");
        }
        else
        {
            text.Append(CultureInfo.InvariantCulture, $"The groups {account} can see, from {directory.From}: {groups.Count:N0}, being every public group and the private ones it is in. It is in {mine:N0} of them. ")
                .Append("Private groups it is not in are not listed, and whether a group it is not in has a team is not known: Teams on the web has handed out no token that can list every group (Microsoft 365's never does; GCC High's does as it gets round to it, and every group is listed here then).\n\n");
        }

        text.Append("| Group | Kind | Visibility | Team | You | Email |\n|---|---|---|---|---|---|\n");
        foreach (var g in groups)
        {
            text.Append(CultureInfo.InvariantCulture, $"| {Cell(g.Name)} | {Cell(g.Kind)} | {Cell(g.Visibility)} | {YesNo(g.Team)} | {You(g)} | {Cell(g.Mail)} |\n");
        }

        text.Append("\n## Details\n");
        foreach (var g in groups)
        {
            text.Append("\n### ").Append(Clean(g.Name)).Append("\n\n");
            void Field(string name, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    text.Append("- ").Append(name).Append(": ").Append(Clean(value)).Append('\n');
                }
            }

            Field("Kind", g.Kind is null ? null : g.Team == true ? $"{g.Kind}, with a team" : g.Kind);
            Field("Visibility", g.Visibility);
            Field("You", You(g));
            Field("Members", g.Members?.ToString("N0", CultureInfo.InvariantCulture));
            Field("Email", g.Mail);
            Field("Alias", g.Alias);
            Field("Site", g.SiteUrl);
            Field("Created", Stamp(g.Created, "yyyy-MM-dd HH:mm"));
            Field("Renewed", Stamp(g.Renewed, "yyyy-MM-dd HH:mm"));
            Field("Expires", Stamp(g.Expires, "yyyy-MM-dd HH:mm"));
            Field("Classification", g.Classification);
            Field("Membership", g.Dynamic ? $"dynamic{(g.MembershipRule is { Length: > 0 } rule ? $", by the rule {rule}" : "")}" : null);
            Field("Synced from on-premises", YesNo(g.OnPremisesSynced));
            Field("Can be given roles", YesNo(g.RoleAssignable));
            Field("Description", g.Description);
            Field("Id", g.Id);
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary><c>groups.md</c> when the groups could not be read: why, rather than a root that fails to list.</summary>
    public static byte[] GroupsUnread(string why) => Encoding.UTF8.GetBytes($"# Groups\n\nThe groups could not be read: {Clean(why)}\n");

    /// <summary><c>groups.md</c> until its first reading lands (it is read behind the listings, which never wait on it).</summary>
    public static byte[] GroupsBeingRead() => Encoding.UTF8.GetBytes("# Groups\n\nThe groups are being read; this file fills in shortly.\n");

    private static string Plural(string kind, int count) => count == 1 ? kind : kind.EndsWith("list", StringComparison.Ordinal) ? kind + "s" : kind.Replace("group", "groups", StringComparison.Ordinal);

    /// <summary>A table cell's text: on one line, its pipes escaped.</summary>
    private static string Cell(string? text) => text is null ? "" : Clean(text).Replace("|", "\\|", StringComparison.Ordinal);

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
