using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace AnyFS.Sources.Teams;

/// <summary>A meeting (or call) a chat held, from its "call ended" message (<c>Event/Call</c> with <c>ended</c>): when, what for, who came and for how long.</summary>
/// <param name="Started">When the call began (<c>conversationStartTime</c>); <paramref name="Ended"/> when it ended (the message's time).</param>
/// <param name="Organiser">The organiser's sign-in name (<c>meetingDetails/organizerUpn</c>).</param>
/// <param name="Kind">Teams' kind of meeting (<c>meetingType</c>: Scheduled, Adhoc…).</param>
/// <param name="ScheduledStart">The meeting's own times (<c>meetingDetails/startTime</c>, <c>endTime</c>), where it was scheduled.</param>
internal sealed record MeetingCall(DateTimeOffset? Started, DateTimeOffset? Ended, string? Subject, string? Organiser, string? Kind,
    DateTimeOffset? ScheduledStart, DateTimeOffset? ScheduledEnd, IReadOnlyList<Attendee> Attended, string? CallId);

/// <summary>Someone who joined a call, and how long they were in it (<c>partlist/part</c>).</summary>
internal sealed record Attendee(string Name, TimeSpan? Time);

/// <summary>
/// A meeting's recording, from its <c>Media_CallRecording</c> message: its video as Teams' media service serves it
/// (<c>amsVideo</c>, to the skype token: MP4, read in ranges), its title, its file name as Teams gave it, when and how long.
/// </summary>
internal sealed record MeetingRecording(string Video, string? Title, string? FileName, DateTimeOffset? At, TimeSpan? Length, string? CallId);

/// <summary>
/// A conversation's meetings and recordings, from its messages (spec 7.9), as a GCC High tenant's are shaped (the work
/// probe, 30-Oct-2026): the markup read as XML, each part looked for by name in either case, what is missing left out.
/// </summary>
internal static class TeamsMeetings
{
    /// <summary>The calls that ended, newest first, each once (by its call id).</summary>
    public static IReadOnlyList<MeetingCall> Calls(IEnumerable<TeamsMessage> messages)
    {
        var calls = new Dictionary<string, MeetingCall>(StringComparer.Ordinal);
        foreach (var message in messages.Where(m => m.MessageType.Equals("Event/Call", StringComparison.OrdinalIgnoreCase) && m.Deleted is null))
        {
            if (Markup(message.Html) is not { } root || Child(root, "ended") is null)
            {
                continue;
            }

            var details = Child(root, "meetingDetails");
            var attended = Children(Child(root, "partlist"), "part")
                .Select(p => new Attendee(Text(p, "displayName") ?? Text(p, "name") ?? p.Attribute("identity")?.Value ?? "?", Length(Text(p, "duration"))))
                .ToList();
            var call = new MeetingCall(Time(Text(root, "conversationStartTime")), message.Composed, Text(details, "subject"), Text(details, "organizerUpn"),
                Text(details, "meetingType"), Time(Text(details, "startTime")), Time(Text(details, "endTime")), attended, Text(root, "callId"));
            calls[call.CallId ?? message.Id] = call;
        }

        return calls.Values.OrderByDescending(c => c.Ended ?? c.Started ?? DateTimeOffset.MinValue).ToList();
    }

    /// <summary>The recordings whose video the media service serves, newest first, each once (by its video's address; its latest message's word).</summary>
    public static IReadOnlyList<MeetingRecording> Recordings(IEnumerable<TeamsMessage> messages)
    {
        var recordings = new Dictionary<string, MeetingRecording>(StringComparer.Ordinal);
        foreach (var message in messages.Where(m => m.MessageType.EndsWith("Media_CallRecording", StringComparison.OrdinalIgnoreCase) && m.Deleted is null)
            .OrderBy(m => m.Composed ?? DateTimeOffset.MinValue))
        {
            if (Markup(message.Html) is not { } root)
            {
                continue;
            }

            var recording = Child(root, "URIObject") ?? root;
            var content = Child(recording, "RecordingContent");
            var video = Children(content, "item").FirstOrDefault(i => string.Equals(i.Attribute("type")?.Value, "amsVideo", StringComparison.OrdinalIgnoreCase))?.Attribute("uri")?.Value;
            if (string.IsNullOrWhiteSpace(video))
            {
                continue;
            }

            var callId = Children(Child(recording, "Identifiers"), "Id")
                .FirstOrDefault(i => string.Equals(i.Attribute("type")?.Value, "callId", StringComparison.OrdinalIgnoreCase))?.Attribute("value")?.Value;
            recordings[video] = new MeetingRecording(video, Text(recording, "Title"), Child(recording, "OriginalName")?.Attribute("v")?.Value,
                Time(content?.Attribute("timestamp")?.Value) ?? message.Composed, Length(content?.Attribute("duration")?.Value), callId);
        }

        return recordings.Values.OrderByDescending(r => r.At ?? DateTimeOffset.MinValue).ToList();
    }

    /// <summary>A message's markup as one XML element (it holds several at its top); null where it is none or does not parse.</summary>
    private static XElement? Markup(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || !content.TrimStart().StartsWith('<'))
        {
            return null;
        }

        try
        {
            return XElement.Parse($"<markup>{content}</markup>", LoadOptions.None);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static XElement? Child(XElement? parent, string name)
        => parent?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<XElement> Children(XElement? parent, string name)
        => parent?.Elements().Where(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? [];

    private static string? Text(XElement? parent, string name) => Child(parent, name)?.Value.Trim() is { Length: > 0 } text ? text : null;

    private static DateTimeOffset? Time(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    /// <summary>A length as Teams writes one: seconds (whole or not), <c>h:mm:ss</c>, or an XML duration (<c>PT1H2M</c>).</summary>
    internal static TimeSpan? Length(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        if (value.Contains(':', StringComparison.Ordinal) && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span))
        {
            return span;
        }

        try
        {
            return value.StartsWith('P') ? XmlConvert.ToTimeSpan(value) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
