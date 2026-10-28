using System.Globalization;
using System.Text.Json;

namespace AnyFS.Sources.GitLab;

/// <summary>Reading GitLab's JSON by hand (no reflection, so the trimmed build keeps it working): a missing or null field is the default.</summary>
internal static class Json
{
    public static string? Str(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long Long(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
        {
            return 0;
        }

        // GraphQL's BigInt comes as a string
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => 0,
        };
    }

    public static bool Bool(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static DateTimeOffset? Date(this JsonElement e, string name)
        => e.Str(name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public static JsonElement Obj(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
}

/// <summary>The signed-in account (<c>GET /user</c>).</summary>
internal sealed record GlUser(long Id, string Username, string Name)
{
    public static GlUser Parse(JsonElement e) => new(e.Long("id"), e.Str("username") ?? "", e.Str("name") ?? e.Str("username") ?? "");
}

/// <summary>A group or subgroup.</summary>
internal sealed record GlGroup(long Id, string Path, string FullPath, string Name, DateTimeOffset? Created)
{
    public static GlGroup Parse(JsonElement e) => new(e.Long("id"), e.Str("path") ?? "", e.Str("full_path") ?? e.Str("path") ?? "", e.Str("name") ?? "", e.Date("created_at"));
}

/// <summary>
/// A project, with what the folders under it depend on: its default branch, whether its repository is empty, and which of
/// its features are on (a feature turned off, or kept from this account, has no folder).
/// </summary>
internal sealed record GlProject(
    long Id,
    string Path,
    string FullPath,
    string Name,
    string NamespacePath,
    string? DefaultBranch,
    bool EmptyRepo,
    DateTimeOffset? Created,
    DateTimeOffset? LastActivity,
    string WebUrl,
    bool Repository,
    bool Issues,
    bool MergeRequests,
    bool Wiki,
    bool Pipelines,
    bool Releases)
{
    public static GlProject Parse(JsonElement e)
    {
        var ns = e.Obj("namespace");
        return new GlProject(
            e.Long("id"),
            e.Str("path") ?? "",
            e.Str("path_with_namespace") ?? "",
            e.Str("name") ?? "",
            ns.Str("full_path") ?? "",
            e.Str("default_branch"),
            e.Bool("empty_repo"),
            e.Date("created_at"),
            e.Date("last_activity_at"),
            e.Str("web_url") ?? "",
            On(e, "repository_access_level", null),
            On(e, "issues_access_level", "issues_enabled"),
            On(e, "merge_requests_access_level", "merge_requests_enabled"),
            On(e, "wiki_access_level", "wiki_enabled"),
            On(e, "builds_access_level", "jobs_enabled"),
            On(e, "releases_access_level", null));
    }

    /// <summary>
    /// A feature is on unless its access level says "disabled" (or, on an older GitLab, its boolean says false). A feature
    /// whose fields are missing (the API's simple form leaves them out) counts as on: asking it shows soon enough.
    /// </summary>
    private static bool On(JsonElement e, string level, string? flag)
    {
        if (e.Str(level) is { } value)
        {
            return !value.Equals("disabled", StringComparison.OrdinalIgnoreCase);
        }

        return flag is null || !e.TryGetProperty(flag, out var v) || v.ValueKind != JsonValueKind.False;
    }
}

/// <summary>An entry of a repository's tree: a blob (file), a tree (folder) or a commit (a submodule).</summary>
internal sealed record GlTreeEntry(string Name, string Type, string Sha, string Mode, string Path = "")
{
    public bool IsTree => Type == "tree";

    public bool IsSubmodule => Type == "commit";

    /// <summary>A file's bytes: a blob (a symbolic link too, whose bytes are where it points).</summary>
    public bool IsBlob => !IsTree && !IsSubmodule;

    public static GlTreeEntry Parse(JsonElement e) => new(e.Str("name") ?? "", e.Str("type") ?? "blob", e.Str("id") ?? "", e.Str("mode") ?? "", e.Str("path") ?? "");
}

/// <summary>A branch or tag: its name and the commit it points at.</summary>
internal sealed record GlRef(string Name, string Commit, DateTimeOffset? Committed)
{
    public static GlRef Parse(JsonElement e)
    {
        var commit = e.Obj("commit");
        return new GlRef(e.Str("name") ?? "", commit.Str("id") ?? "", commit.Date("committed_date") ?? commit.Date("created_at"));
    }
}

/// <summary>A blob's size, from GraphQL: its own, or for a file in Git LFS the object's (<c>rawSize</c>).</summary>
internal sealed record GlBlobSize(string Path, long Size, bool Lfs);
