using System.Text.Json.Serialization;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Atlassian.Jira;

/// <summary>When a folder of issues was last read whole, and last read at all (whole, or the changes since): where the next read's window starts.</summary>
internal sealed record JiraTreeRead(DateTimeOffset Whole, DateTimeOffset At);

/// <summary>
/// When each folder of issues was last read, kept between runs beside the listings those reads made. It was known for
/// the run alone, so the first walk of each run read every project that had changed whole, every issue with every field,
/// where the changes since the last run's read were a search or two (the review of 21-Nov-2026, P12). For the listings
/// kept under the same key and no others; written when a source stops, after its listings are saved, and not at all
/// where they could not be: a time later than the listings it speaks of would hide what changed between.
/// </summary>
internal sealed record JiraTreeReads(int Version, string Key, Dictionary<string, JiraTreeRead> Reads)
{
    private const int Current = 1;

    public static Dictionary<string, JiraTreeRead>? Load(string path, string key)
        => StateFile.Load(path, JiraTreeReadsJson.Default.JiraTreeReads) is { Version: Current, Reads: not null } kept && kept.Key == key ? kept.Reads : null;

    public static void Save(string path, string key, Dictionary<string, JiraTreeRead> reads)
        => StateFile.Save(path, new JiraTreeReads(Current, key, reads), JiraTreeReadsJson.Default.JiraTreeReads);
}

[JsonSerializable(typeof(JiraTreeReads))]
internal sealed partial class JiraTreeReadsJson : JsonSerializerContext;
