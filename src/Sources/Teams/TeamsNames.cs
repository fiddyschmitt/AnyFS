using AnyFS.Sdk;

namespace AnyFS.Sources.Teams;

/// <summary>
/// The names Teams' things show as (spec 7.9), by the SDK's <see cref="SafeNames"/>: a team, channel, chat or thread
/// folder made safe for Windows and cut at <see cref="MaxName"/>, and two that would be one name in either case told apart
/// with a number after the whole name (a chat's name is no file name: no extension to keep).
/// </summary>
internal static class TeamsNames
{
    public const int MaxName = 200;

    public static string Safe(string name) => SafeNames.Safe(name, MaxName);

    /// <summary>
    /// Display names for a set of things in order: each made <see cref="Safe"/>, twins in case numbered after the first; one
    /// that would be a <paramref name="reserved"/> name (a file or folder the source puts beside them) numbered too.
    /// </summary>
    public static IReadOnlyList<string> Unique(IReadOnlyList<string> raw, IEnumerable<string>? reserved = null)
        => SafeNames.Unique(raw, reserved, Safe, static (name, n) => $"{name} ({n})");
}
