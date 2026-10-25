using System.Text.Json.Serialization;

namespace AnyFS.Sources.Outlook.Mail;

/// <summary>A folder's part of a kept listing: its sync state and the items its server told of, or the calendar view last read.</summary>
/// <param name="IsCalendarView">Whether <paramref name="Items"/> is a calendar's view over its window (read afresh on each refresh) rather than a folder's synced items.</param>
internal sealed record FolderIndex(string FolderId, string? SyncState, IReadOnlyList<MailItemSummary> Items, bool IsCalendarView);

/// <summary>
/// A mailbox's listing kept between starts in its source's state folder (spec 7.7: the listing index, never a message;
/// subjects, senders, dates and ids only), so a restart serves the last listing at once and asks the server only what
/// changed since, from each folder's sync state, instead of listing every folder whole again. Kept with whose mailbox it
/// is and the settings that shape the listing, so a source pointed elsewhere, or shaped otherwise, starts afresh.
/// </summary>
/// <param name="Shape">The settings the listing depends on (hidden items shown, the calendar window), as one string.</param>
internal sealed record MailboxIndex(int Version, string Mailbox, string Shape, DateTimeOffset Taken, IReadOnlyList<MailFolder> Folders, IReadOnlyList<FolderIndex> States);

internal static class MailboxIndexFile
{
    /// <summary>Bumped when the shape changes; an older file is then not read.</summary>
    public const int Version = 1;

    public const string FileName = "mailbox.json.gz";

    public static void Save(string path, MailboxIndex index) => StateFile.Save(path, index, MailboxIndexJson.Default.MailboxIndex);

    /// <summary>The listing kept at <paramref name="path"/>, or null when there is none, it cannot be read, or it is another mailbox's or shaped otherwise.</summary>
    public static MailboxIndex? Load(string path, string mailbox, string shape)
        => StateFile.Load(path, MailboxIndexJson.Default.MailboxIndex) is { Version: Version } index && index.Mailbox == mailbox && index.Shape == shape ? index : null;
}

/// <summary>The kept listing's JSON, generated at build time (no reflection, so trimming keeps it working).</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(MailboxIndex))]
internal sealed partial class MailboxIndexJson : JsonSerializerContext;
