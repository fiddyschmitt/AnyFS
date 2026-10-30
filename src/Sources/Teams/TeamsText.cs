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
    /// <c>groups.md</c> (the account's groups) or <c>all-groups.md</c> (every group): what the groups are and where they came
    /// from, a table of them all (name, kind, visibility, team, the account's part, email), then each one's details, every
    /// field known. Sorted by name.
    /// </summary>
    public static byte[] Groups(GroupsDirectory directory, string account)
    {
        var groups = directory.Groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id, StringComparer.Ordinal).ToList();
        string You(GroupInfo g) => directory.Owner.Contains(g.Id) ? "owner" : directory.Member.Contains(g.Id) ? "member" : "";
        string YesNo(bool? b) => b switch { true => "yes", false => "no", _ => "" };

        var text = new StringBuilder(directory.Scope is GroupsScope.Organisation or GroupsScope.Visible ? "# All groups\n\n" : "# Your groups\n\n");
        var mine = groups.Count(g => You(g).Length > 0);
        var kinds = string.Join(", ", groups.GroupBy(g => g.Kind ?? "Group").OrderByDescending(k => k.Count()).Select(k => $"{k.Count():N0} {Plural(k.Key, k.Count())}"));
        switch (directory.Scope)
        {
            case GroupsScope.Organisation:
                text.Append(CultureInfo.InvariantCulture, $"Every group in the organisation, as {directory.From} lists them for {account}: {groups.Count:N0} ({kinds}; {groups.Count(g => g.Team == true):N0} with a team). ")
                    .Append(CultureInfo.InvariantCulture, $"{account} is in {mine:N0} of them (groups.md lists those).\n\n");
                break;
            case GroupsScope.Visible:
                text.Append(CultureInfo.InvariantCulture, $"The groups {account} can see, from {directory.From}: {groups.Count:N0}, being every public group and the private ones it is in. It is in {mine:N0} of them. ")
                    .Append("Private groups it is not in are not listed, and whether a group it is not in has a team is not known: Teams on the web has handed out no token that can list every group (Microsoft 365's never does; GCC High's does as it gets round to it, and every group is listed here then).\n\n");
                break;
            case GroupsScope.Yours:
                text.Append(CultureInfo.InvariantCulture, $"The groups {account} is in, as {directory.From} lists them: {groups.Count:N0} ({kinds}). It owns {groups.Count(g => directory.Owner.Contains(g.Id)):N0} of them. ")
                    .Append("Every group in the organisation is in all-groups.md.\n\n");
                break;
            default:
                text.Append(CultureInfo.InvariantCulture, $"The groups of {account}'s own teams, from {directory.From}: {groups.Count:N0}. ")
                    .Append("Its other groups need Microsoft Graph's token, which Teams on the web hands out in GCC High (as it gets round to it; AnyFS asks for it in the background) and never in Microsoft 365.\n\n");
                break;
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

    /// <summary><c>groups.md</c> or <c>all-groups.md</c> when the groups could not be read: why, rather than a root that fails to list.</summary>
    public static byte[] GroupsUnread(string why) => Unread("Groups", "The groups", why);

    /// <summary>A file built behind the listings (groups.md, calls.md…) when what it shows could not be read: why.</summary>
    public static byte[] Unread(string title, string what, string why) => Encoding.UTF8.GetBytes($"# {title}\n\n{what} could not be read: {Clean(why)}\n");

    /// <summary><c>groups.md</c> or <c>all-groups.md</c> until its first reading lands (each is read behind the listings, which never wait on it).</summary>
    public static byte[] GroupsBeingRead() => Encoding.UTF8.GetBytes("# Groups\n\nThe groups are being read; this file fills in shortly (a large organisation's take minutes).\n");

    private static string Plural(string kind, int count) => count == 1 ? kind : kind.EndsWith("list", StringComparison.Ordinal) ? kind + "s" : kind.Replace("group", "groups", StringComparison.Ordinal);

    /// <summary>
    /// <c>calls.md</c>: the account's call history, newest first: when, which way, with whom, what kind, how it went, and how
    /// long it lasted (from the answer to the end).
    /// </summary>
    public static byte[] Calls(IReadOnlyList<CallRecord> calls)
    {
        var text = new StringBuilder("# Calls\n\n");
        text.Append(CultureInfo.InvariantCulture, $"The account's call history as Teams keeps it: {calls.Count:N0} call{(calls.Count == 1 ? "" : "s")}, newest first.\n");
        if (calls.Count > 0)
        {
            text.Append("\n| When | Which way | With | Kind | How it went | Length |\n|---|---|---|---|---|---|\n");
            foreach (var call in calls)
            {
                var incoming = string.Equals(call.Direction, "incoming", StringComparison.OrdinalIgnoreCase);
                var with = incoming ? call.Originator : call.Target;
                var others = call.Participants > 2 ? $"{with ?? "?"} (+{call.Participants - 2})" : with;
                var length = call.Connected is { } from && call.End is { } to && to > from ? Duration(to - from) : "";
                text.Append(CultureInfo.InvariantCulture, $"| {Stamp(call.Start, "yyyy-MM-dd HH:mm")} | {Cell(incoming ? "In" : string.Equals(call.Direction, "outgoing", StringComparison.OrdinalIgnoreCase) ? "Out" : call.Direction)} | {Cell(others)} | {Cell(CallKind(call.Type))} | {Cell(Outcome(call.State))} | {length} |\n");
            }
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static string? CallKind(string? type) => type?.ToLowerInvariant() switch
    {
        "twoparty" => "1:1",
        "group" or "multiparty" => "Group",
        "meeting" => "Meeting",
        _ => Words(type),
    };

    private static string? Outcome(string? state) => state?.ToLowerInvariant() switch
    {
        "accepted" => "Answered",
        "missed" => "Missed",
        "declined" => "Declined",
        "voicemail" => "Voicemail",
        "transferred" => "Transferred",
        "forwarded" => "Forwarded",
        _ => Words(state),
    };

    private static string Duration(TimeSpan span) => span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>activity.md</c>: the account's activity feed as Teams keeps it, newest first: its mentions, then everything else
    /// (replies, reactions, being added to a team…): when, what, who, where, and the start of the message.
    /// </summary>
    public static byte[] Activity(IReadOnlyList<ActivityItem> items, Func<ActivityItem, string?> whereOf)
    {
        var mentions = items.Where(i => i.Mention).ToList();
        var others = items.Where(i => !i.Mention).ToList();
        var text = new StringBuilder("# Activity\n\n");
        text.Append(CultureInfo.InvariantCulture, $"The account's activity feed as Teams keeps it: {mentions.Count:N0} mention{(mentions.Count == 1 ? "" : "s")} and {others.Count:N0} other entr{(others.Count == 1 ? "y" : "ies")}, newest first.\n");
        Section("Mentions", mentions);
        Section("Everything else", others);
        return Encoding.UTF8.GetBytes(text.ToString());

        void Section(string title, List<ActivityItem> list)
        {
            text.Append("\n## ").Append(title).Append("\n\n");
            if (list.Count == 0)
            {
                text.Append("None.\n");
                return;
            }

            text.Append("| When | What | Who | Where | Message |\n|---|---|---|---|---|\n");
            foreach (var item in list)
            {
                text.Append(CultureInfo.InvariantCulture, $"| {Stamp(item.When, "yyyy-MM-dd HH:mm")} | {Cell(What(item))} | {Cell(item.From)} | {Cell(whereOf(item))} | {Cell(Shorten(item.Preview, 120))} |\n");
            }
        }
    }

    /// <summary>What an activity was, in words: a mention, a reply, a reaction, else Teams' own name for it.</summary>
    private static string What(ActivityItem item)
    {
        var kind = $"{item.Type} {item.Subtype}";
        return item.Mention || kind.Contains("mention", StringComparison.OrdinalIgnoreCase) ? "Mentioned you"
            : kind.Contains("reply", StringComparison.OrdinalIgnoreCase) ? "Replied"
            : kind.Contains("reaction", StringComparison.OrdinalIgnoreCase) || kind.Contains("like", StringComparison.OrdinalIgnoreCase) ? "Reacted"
            : string.Equals(item.Subtype, "addedToTeam", StringComparison.OrdinalIgnoreCase) ? "Added you to a team"
            : Words(item.Subtype ?? item.Type) ?? "Activity";
    }

    /// <summary>Teams' camel-case names in words: <c>privateMeetingCreated</c> is "Private meeting created".</summary>
    private static string? Words(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var spaced = string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]) ? " " + char.ToLowerInvariant(c) : c.ToString()));
        return char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    private static string? Shorten(string? text, int length)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var clean = Clean(text);
        return clean.Length <= length ? clean : clean[..length].TrimEnd() + "…";
    }

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
