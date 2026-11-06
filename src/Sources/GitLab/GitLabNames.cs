using AnyFS.Sdk;

namespace AnyFS.Sources.GitLab;

/// <summary>
/// The names GitLab's things show as (spec 7.8), by the SDK's <see cref="SafeNames"/>: a repository's files keep their own
/// names wherever Windows takes them, and two that differ only in case (git keeps <c>README</c> and <c>readme</c> apart,
/// AnyFS's paths do not) are told apart with a number. Issues and merge requests are named by their number and title, cut
/// to a length that leaves room for the path above them.
/// </summary>
internal static class GitLabNames
{
    /// <summary>The longest title kept in an issue's or merge request's folder name.</summary>
    public const int MaxTitle = 80;

    /// <summary>A name Windows takes: refused characters become underscores, a refused ending or device name gets one more, an overlong name is cut.</summary>
    public static string Safe(string name) => SafeNames.Safe(name);

    /// <summary>Display names for a folder's entries, in their order, each made <see cref="Safe"/> and twins in case numbered (<c>readme (2)</c>); raw names that need no change keep theirs.</summary>
    public static IReadOnlyList<string> Unique(IReadOnlyList<string> raw) => SafeNames.Unique(raw);

    /// <summary><c>readme (2).md</c> from <c>readme.md</c>; <c>Makefile (2)</c> from <c>Makefile</c>.</summary>
    public static string Numbered(string name, int n) => SafeNames.Numbered(name, n);

    /// <summary>An issue's folder: <c>#12 Install fails on Debian</c>.</summary>
    public static string Issue(long iid, string title) => Safe($"#{iid} {Title(title)}");

    /// <summary>A merge request's folder: <c>!7 Docs_ say more</c>.</summary>
    public static string MergeRequest(long iid, string title) => Safe($"!{iid} {Title(title)}");

    /// <summary>A title for a folder name: one line, its spaces collapsed, cut on a character boundary.</summary>
    public static string Title(string title) => SafeNames.Title(title, MaxTitle);
}
