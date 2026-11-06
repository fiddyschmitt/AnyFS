using System.Text.Json;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Atlassian.Bitbucket;

/// <summary>What a Bitbucket source's address names (spec 7.11).</summary>
internal enum BitbucketScopeKind
{
    /// <summary>The whole server: every project the account can see, and its repositories.</summary>
    Server,

    /// <summary>One project's repositories (a person's, <c>~name</c>, too).</summary>
    Project,

    /// <summary>One repository.</summary>
    Repository,
}

/// <summary>A Bitbucket source's scope: what kind, the project's key and the repository's slug.</summary>
internal sealed record BitbucketScope(BitbucketScopeKind Kind, string ProjectKey, string Slug)
{
    public static readonly BitbucketScope Server = new(BitbucketScopeKind.Server, "", "");

    /// <summary>What the scope names, for messages: <c>ABC</c>, <c>ABC/docs</c>, or empty for the server.</summary>
    public string Value => Kind switch
    {
        BitbucketScopeKind.Repository => $"{ProjectKey}/{Slug}",
        BitbucketScopeKind.Project => ProjectKey,
        _ => "",
    };

    /// <summary>The REST path of the project, or of a person's projects: <c>projects/ABC</c>, <c>projects/~jane</c>.</summary>
    public static string ProjectPath(string key) => "projects/" + Uri.EscapeDataString(key);

    /// <summary>The REST path of a repository: <c>projects/ABC/repos/docs</c>.</summary>
    public static string RepositoryPath(string key, string slug) => $"{ProjectPath(key)}/repos/{Uri.EscapeDataString(slug)}";

    /// <summary>
    /// The scope the rest of an address names (below the installation): a project (<c>projects/ABC</c>, and its pages), a
    /// repository (<c>projects/ABC/repos/docs</c> and its pages: browse, commits, branches, pull requests; a clone address
    /// <c>scm/abc/docs.git</c>; a person's <c>users/jane/repos/docs</c>), else the whole server for the dashboard or nothing
    /// below it. Null, with why, for an address that names nothing a source can show.
    /// </summary>
    public static BitbucketScope? From(AtlassianAddress address, out string? problem)
    {
        problem = null;
        var rest = address.Rest;
        switch (rest.Count)
        {
            case 0:
                return Server;
            case 1 when rest[0].Equals("dashboard", StringComparison.OrdinalIgnoreCase) || rest[0].Equals("projects", StringComparison.OrdinalIgnoreCase) || rest[0].Equals("repos", StringComparison.OrdinalIgnoreCase):
                return Server;
        }

        if (rest[0].Equals("projects", StringComparison.OrdinalIgnoreCase) && rest.Count >= 2 && rest[1].Length > 0)
        {
            var key = Key(rest[1]);
            return rest.Count >= 4 && rest[2].Equals("repos", StringComparison.OrdinalIgnoreCase) && rest[3].Length > 0
                ? new BitbucketScope(BitbucketScopeKind.Repository, key, rest[3].ToLowerInvariant())
                : new BitbucketScope(BitbucketScopeKind.Project, key, "");
        }

        if (rest[0].Equals("users", StringComparison.OrdinalIgnoreCase) && rest.Count >= 2 && rest[1].Length > 0)
        {
            var key = "~" + rest[1].TrimStart('~').ToUpperInvariant();
            return rest.Count >= 4 && rest[2].Equals("repos", StringComparison.OrdinalIgnoreCase) && rest[3].Length > 0
                ? new BitbucketScope(BitbucketScopeKind.Repository, key, rest[3].ToLowerInvariant())
                : new BitbucketScope(BitbucketScopeKind.Project, key, "");
        }

        if (rest[0].Equals("scm", StringComparison.OrdinalIgnoreCase) && rest.Count == 3)
        {
            var slug = rest[2].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? rest[2][..^4] : rest[2];
            return new BitbucketScope(BitbucketScopeKind.Repository, Key(rest[1]), slug.ToLowerInvariant());
        }

        problem = "The address names nothing a Bitbucket source shows: give the server's address, a project's (…/projects/ABC) or a repository's (…/projects/ABC/repos/docs).";
        return null;
    }

    /// <summary>A project's key as Bitbucket writes it: upper case, a person's with its tilde.</summary>
    private static string Key(string segment) => segment.StartsWith('~') ? "~" + segment[1..].ToUpperInvariant() : segment.ToUpperInvariant();

    /// <summary>The project key a project folder's name ends with (<c>Alpha (ABC)</c>), or null.</summary>
    public static string? KeyOfProjectFolder(string name) => AtlassianNames.KeyInParentheses(name);

    /// <summary>The number a pull request folder's name starts with (<c>#12 Title</c>), or null.</summary>
    public static long? NumberOfFolder(string name) => AtlassianNames.NumberAfterHash(name);
}

/// <summary>A project, as the project list and the project call give it.</summary>
internal sealed record BbProject(string Key, long Id, string Name, string? Description, bool Public, string Type)
{
    public static BbProject Parse(JsonElement e) => new(e.Str("key") ?? "", e.Long("id"), e.Str("name") ?? e.Str("key") ?? "", e.Str("description"), e.Bool("public"), e.Str("type") ?? "NORMAL");
}

/// <summary>A repository, as a project's repository list and the repository call give it.</summary>
internal sealed record BbRepository(string Slug, long Id, string Name, string ProjectKey, string? ProjectName, string State, bool Public, string? Description)
{
    public static BbRepository Parse(JsonElement e) => new(
        e.Str("slug") ?? "",
        e.Long("id"),
        e.Str("name") ?? e.Str("slug") ?? "",
        e.Obj("project").Str("key") ?? "",
        e.Obj("project").Str("name"),
        e.Str("state") ?? "AVAILABLE",
        e.Bool("public"),
        e.Str("description"));

    /// <summary>The REST path of the repository's calls: <c>projects/ABC/repos/docs</c>.</summary>
    public string Path => BitbucketScope.RepositoryPath(ProjectKey, Slug);

    /// <summary>The repository's page: <c>projects/ABC/repos/docs</c> under the installation.</summary>
    public string Web => $"projects/{Uri.EscapeDataString(ProjectKey)}/repos/{Uri.EscapeDataString(Slug)}";
}

/// <summary>A branch or a tag: its full id, the name people use, and the commit it is at.</summary>
internal sealed record BbRef(string Id, string DisplayId, string LatestCommit, bool IsDefault)
{
    public static BbRef Parse(JsonElement e) => new(e.Str("id") ?? "", e.Str("displayId") ?? "", e.Str("latestCommit") ?? "", e.Bool("isDefault"));
}

/// <summary>A commit: its id, when it was made, who made it and what it says.</summary>
internal sealed record BbCommit(string Id, string DisplayId, DateTimeOffset? AuthorTime, DateTimeOffset? CommitTime, AtlassianUser? Author, string? Message)
{
    public static BbCommit Parse(JsonElement e) => new(
        e.Str("id") ?? "",
        e.Str("displayId") ?? (e.Str("id") is { Length: >= 11 } id ? id[..11] : e.Str("id") ?? ""),
        Json.Epoch(e, "authorTimestamp"),
        Json.Epoch(e, "committerTimestamp") ?? Json.Epoch(e, "authorTimestamp"),
        AtlassianUser.Person(e.Obj("author")),
        e.Str("message"));
}

/// <summary>One entry of a repository folder at a commit, as the browse call lists them.</summary>
/// <param name="Kind">FILE, DIRECTORY or SUBMODULE.</param>
internal sealed record BbTreeEntry(string Name, string Kind, long Size, string? ContentId)
{
    public static BbTreeEntry Parse(JsonElement e) => new(e.Obj("path").Str("name") ?? e.Obj("path").Str("toString") ?? "", e.Str("type") ?? "FILE", e.Long("size"), e.Str("contentId"));

    public bool IsDirectory => Kind.Equals("DIRECTORY", StringComparison.OrdinalIgnoreCase);

    public bool IsSubmodule => Kind.Equals("SUBMODULE", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A pull request as its list and its call give it: what its folder's name and its <c>pull-request.md</c> show.</summary>
internal sealed record BbPullRequest(long Id, long Version, string Title, string? Description, string State, DateTimeOffset? Created, DateTimeOffset? Updated, DateTimeOffset? Closed,
    AtlassianUser? Author, string FromBranch, string FromCommit, string ToBranch, string ToCommit, IReadOnlyList<(AtlassianUser User, string Status)> Reviewers)
{
    public static BbPullRequest Parse(JsonElement e)
    {
        var from = e.Obj("fromRef");
        var to = e.Obj("toRef");
        var reviewers = e.Arr("reviewers").Select(r => (User: AtlassianUser.Person(r.Obj("user")), Status: r.Str("status") ?? (r.Bool("approved") ? "APPROVED" : "UNAPPROVED")))
            .Where(r => r.User is not null).Select(r => (r.User!, r.Status)).ToList();
        return new BbPullRequest(
            e.Long("id"),
            e.Long("version"),
            e.Str("title") ?? "",
            e.Str("description"),
            e.Str("state") ?? "OPEN",
            Json.Epoch(e, "createdDate"),
            Json.Epoch(e, "updatedDate"),
            Json.Epoch(e, "closedDate"),
            AtlassianUser.Person(e.Obj("author")),
            from.Str("displayId") ?? "",
            from.Str("latestCommit") ?? "",
            to.Str("displayId") ?? "",
            to.Str("latestCommit") ?? "",
            reviewers);
    }

    public bool Open => State.Equals("OPEN", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A comment on a pull request (its replies with it), as an activity carries it.</summary>
internal sealed record BbComment(long Id, AtlassianUser? Author, DateTimeOffset? Created, DateTimeOffset? Updated, string Text, string? Anchor, IReadOnlyList<BbComment> Replies)
{
    public static BbComment Parse(JsonElement e, JsonElement anchor)
    {
        string? at = null;
        if (anchor.Str("path") is { Length: > 0 } path)
        {
            at = anchor.Str("line") is { } line ? $"{path}:{line}" : path;
        }

        return new BbComment(
            e.Long("id"),
            AtlassianUser.Person(e.Obj("author")),
            Json.Epoch(e, "createdDate"),
            Json.Epoch(e, "updatedDate"),
            e.Str("text") ?? "",
            at,
            e.Arr("comments").Select(c => Parse(c, default)).ToList());
    }
}

/// <summary>Something that happened to a pull request, as its activities list them: a comment, an approval, a merge.</summary>
internal sealed record BbActivity(long Id, DateTimeOffset? Created, AtlassianUser? User, string Action, BbComment? Comment)
{
    public static BbActivity Parse(JsonElement e) => new(
        e.Long("id"),
        Json.Epoch(e, "createdDate"),
        AtlassianUser.Person(e.Obj("user")),
        e.Str("action") ?? "",
        e.Obj("comment") is { ValueKind: JsonValueKind.Object } comment ? BbComment.Parse(comment, e.Obj("commentAnchor")) : null);
}
