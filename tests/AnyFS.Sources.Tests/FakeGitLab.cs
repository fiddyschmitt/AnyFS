using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnyFS.Sources.Common;
using AnyFS.Sources.GitLab;

namespace AnyFS.Sources.Tests;

/// <summary>
/// A stand-in for a GitLab server when the lab's is not there (spec 7.8): one model of users, groups, projects and their
/// repositories (commits as snapshots of files; branches and tags pointing at them; Git LFS objects), answered through
/// <see cref="Handler"/> as GitLab 19 answered the lab's probes (GITLAB-NOTES): REST v4 with offset paging (Link, X-Total)
/// and keyset paging for a tree, GraphQL's blob sizes, raw files, 401 for a refused sign-in, 404 for what an account may
/// not see, 429 with Retry-After when told. Everything is private: an account sees what it or a group above it is a
/// member of. The other features (wiki, issues, merge requests, pipelines, releases) and the sign-in pages are in the
/// other parts of this class.
/// </summary>
internal sealed partial class FakeGitLab
{
    private readonly object _gate = new();
    private readonly List<User> _users = [];
    private readonly List<Group> _groups = [];
    private readonly List<Project> _projects = [];
    private int _nextId = 100;

    /// <param name="origin">The scheme and host (<c>https://gitlab.fake.test</c>).</param>
    /// <param name="root">The path GitLab is installed at (<c>/gitlab</c>), or empty for the host's root.</param>
    public FakeGitLab(string origin = "https://gitlab.fake.test", string root = "")
    {
        Origin = origin.TrimEnd('/');
        Root = root.TrimEnd('/');
        ExternalUrl = Origin + Root;

        // the source paces itself for a real server; a suite of thousands of requests need not wait
        RequestPace.Use("gitlab:" + new Uri(Origin).Host, new RequestPace(burst: 1_000_000, perSecond: 1_000));
    }

    public string Origin { get; }

    public string Root { get; }

    /// <summary>The installation's address (what a source's address starts with).</summary>
    public string Server => Origin + Root;

    /// <summary>Every request answered, oldest first: "GET /api/v4/projects/1".</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>The address GitLab is configured with (its external URL): what its Link headers and web addresses start with.</summary>
    public string ExternalUrl { get; set; } = "";

    /// <summary>Whether GraphQL answers (an old or locked-down server refuses it: the source asks each file's size instead).</summary>
    public bool GraphQl { get; set; } = true;

    /// <summary>How many requests to answer 429 with Retry-After: 1 before answering again.</summary>
    public int ThrottleNext { get; set; }

    /// <summary>The project at a full path (for a test to change).</summary>
    public Project ProjectAt(string fullPath)
    {
        lock (_gate)
        {
            return _projects.Single(p => p.FullPath == fullPath);
        }
    }

    /// <summary>How many requests matched <paramref name="what"/> (a substring of "METHOD /path?query").</summary>
    public int Count(string what) => Requests.Count(r => r.Contains(what, StringComparison.Ordinal));

    // --- the model -------------------------------------------------------------------------------------------------------

    public sealed class User(long id, string username, string name)
    {
        public long Id { get; } = id;

        public string Username { get; } = username;

        public string Name { get; } = name;

        public List<string> Tokens { get; } = [];

        public string? Password { get; set; }
    }

    public sealed class Group(long id, string path, string name, Group? parent)
    {
        public long Id { get; } = id;

        public string Path { get; } = path;

        public string Name { get; } = name;

        public Group? Parent { get; } = parent;

        public string FullPath => Parent is null ? Path : Parent.FullPath + "/" + Path;

        public HashSet<long> Members { get; } = [];

        public List<WikiPage> WikiPages { get; } = [];

        public Dictionary<string, byte[]> WikiUploads { get; } = new(StringComparer.Ordinal);
    }

    public sealed partial class Project(FakeGitLab owner, long id, string path, string name, string namespacePath, Group? group)
    {
        public long Id { get; } = id;

        public string Path { get; } = path;

        public string Name { get; } = name;

        public string NamespacePath { get; } = namespacePath;

        public Group? Group { get; } = group;

        public string FullPath => NamespacePath + "/" + Path;

        public string DefaultBranch { get; set; } = "main";

        public HashSet<long> Members { get; } = [];

        public Dictionary<string, string> Branches { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Commit> Commits { get; } = new(StringComparer.Ordinal);

        public bool Issues { get; set; } = true;

        public bool MergeRequests { get; set; } = true;

        public bool Wiki { get; set; } = true;

        public bool Pipelines { get; set; } = true;

        public List<WikiPage> WikiPages { get; } = [];

        /// <summary>The wiki's attachments by "hash/name".</summary>
        public Dictionary<string, byte[]> WikiUploads { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset Created { get; } = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        /// <summary>A commit of these files (path → bytes; a path under <see cref="Lfs"/> goes to Git LFS) on <paramref name="branch"/>, made the branch's head.</summary>
        public string Commit(string branch, IReadOnlyDictionary<string, byte[]> files, DateTimeOffset? when = null, IEnumerable<string>? lfs = null, IEnumerable<string>? submodules = null)
        {
            lock (owner._gate)
            {
                var commit = new Commit(files, lfs?.ToHashSet(StringComparer.Ordinal) ?? [], submodules?.ToHashSet(StringComparer.Ordinal) ?? [], when ?? Created.AddDays(Commits.Count + 1));
                Commits[commit.Id] = commit;
                Branches[branch] = commit.Id;
                return commit.Id;
            }
        }

        public bool EmptyRepo => Commits.Count == 0;
    }

    /// <summary>A snapshot of a repository: its files, those of them in Git LFS, and its submodules (paths only).</summary>
    public sealed class Commit(IReadOnlyDictionary<string, byte[]> files, HashSet<string> lfs, HashSet<string> submodules, DateTimeOffset when)
    {
        public string Id { get; } = Sha1(string.Join('\n', files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Key + ":" + Sha1(f.Value))) + when.ToString("O", CultureInfo.InvariantCulture));

        public IReadOnlyDictionary<string, byte[]> Files { get; } = files;

        public HashSet<string> Lfs { get; } = lfs;

        public HashSet<string> Submodules { get; } = submodules;

        public DateTimeOffset When { get; } = when;

        /// <summary>The blob git keeps for a file: the bytes, or for a file in LFS its pointer.</summary>
        public byte[] Blob(string path) => Lfs.Contains(path)
            ? Encoding.ASCII.GetBytes($"version https://git-lfs.github.com/spec/v1\noid sha256:{Convert.ToHexStringLower(SHA256.HashData(Files[path]))}\nsize {Files[path].Length}\n")
            : Files[path];

        /// <summary>A folder's entries: (name, "tree"|"blob"|"commit", id, mode), trees first as GitLab lists them.</summary>
        public List<(string Name, string Type, string Id, string Mode)> Tree(string folder)
        {
            var prefix = folder.Length == 0 ? "" : folder + "/";
            var trees = new SortedSet<string>(StringComparer.Ordinal);
            var blobs = new List<(string, string, string, string)>();
            foreach (var path in Files.Keys.Concat(Submodules))
            {
                if (!path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var rest = path[prefix.Length..];
                var slash = rest.IndexOf('/');
                if (slash >= 0)
                {
                    trees.Add(rest[..slash]);
                }
                else if (Submodules.Contains(path))
                {
                    blobs.Add((rest, "commit", Sha1(path), "160000"));
                }
                else
                {
                    blobs.Add((rest, "blob", Sha1(Blob(path)), "100644"));
                }
            }

            return trees.Select(t => (t, "tree", Sha1(Id + prefix + t), "040000")).Concat(blobs.OrderBy(b => b.Item1, StringComparer.Ordinal)).ToList();
        }

        public bool IsFolder(string folder) => folder.Length == 0 || Files.Keys.Concat(Submodules).Any(p => p.StartsWith(folder + "/", StringComparison.Ordinal));
    }

    public User AddUser(string username, string name, string? token = null)
    {
        lock (_gate)
        {
            var user = new User(_nextId++, username, name);
            if (token is not null)
            {
                user.Tokens.Add(token);
            }

            _users.Add(user);
            return user;
        }
    }

    public Group AddGroup(string path, string name, Group? parent = null, params User[] members)
    {
        lock (_gate)
        {
            var group = new Group(_nextId++, path, name, parent);
            foreach (var m in members)
            {
                group.Members.Add(m.Id);
            }

            _groups.Add(group);
            return group;
        }
    }

    public Project AddProject(Group group, string path, string name, params User[] members) => AddProject(group, null, path, name, members);

    /// <summary>A project in a user's own namespace.</summary>
    public Project AddPersonalProject(User owner, string path, string name) => AddProject(null, owner, path, name, [owner]);

    private Project AddProject(Group? group, User? owner, string path, string name, User[] members)
    {
        lock (_gate)
        {
            var project = new Project(this, _nextId++, path, name, group?.FullPath ?? owner!.Username, group);
            foreach (var m in members)
            {
                project.Members.Add(m.Id);
            }

            _projects.Add(project);
            return project;
        }
    }

    private bool Sees(User user, Group group) => Lineage(group).Any(g => g.Members.Contains(user.Id));

    private bool Sees(User user, Project project) => project.Members.Contains(user.Id) || project.Group is { } g && Sees(user, g);

    private static IEnumerable<Group> Lineage(Group group)
    {
        for (var g = group; g is not null; g = g.Parent)
        {
            yield return g;
        }
    }

    // --- answering -------------------------------------------------------------------------------------------------------

    /// <summary>A handler that answers in-process, without a socket.</summary>
    public HttpMessageHandler Handler() => new InProcessHandler(this);

    /// <summary>A stand-in for the source: its requests come here.</summary>
    public GitLabStandIn StandIn() => new(Handler);

    internal sealed record Request(string Method, Uri Url, string[] Segments, Dictionary<string, string> Query, Dictionary<string, string> Headers, byte[] Body)
    {
        public string? Q(string name) => Query.TryGetValue(name, out var v) ? v : null;
    }

    internal sealed record Response(int Status, byte[] Body, string? ContentType = "application/json", Dictionary<string, string>? Headers = null)
    {
        public static Response Json(JsonNode node, int status = 200, Dictionary<string, string>? headers = null) => new(status, Encoding.UTF8.GetBytes(node.ToJsonString()), "application/json", headers);

        public static Response Message(int status, string message) => Json(new JsonObject { ["message"] = message }, status);

        public static Response NotFound(string what = "404 Not Found") => Message(404, what);
    }

    private Response Answer(Request r)
    {
        Requests.Enqueue($"{r.Method} {r.Url.AbsolutePath}{r.Url.Query}");
        lock (_gate)
        {
            if (ThrottleNext > 0)
            {
                ThrottleNext--;
                return Response.Json(new JsonObject { ["message"] = "Retry later" }, 429, new() { ["Retry-After"] = "1" });
            }

            var path = r.Url.AbsolutePath;
            if (Root.Length > 0)
            {
                if (!path.StartsWith(Root + "/", StringComparison.Ordinal))
                {
                    return new Response(404, Encoding.UTF8.GetBytes("<html>not here</html>"), "text/html");
                }

                path = path[Root.Length..];
            }

            if (AnswerPages(r, path) is { } page)
            {
                return page;
            }

            if (!path.StartsWith("/api/", StringComparison.Ordinal))
            {
                return new Response(404, Encoding.UTF8.GetBytes("<html>no such page</html>"), "text/html");
            }

            var user = Signed(r);
            if (path == "/api/graphql")
            {
                return user is null ? Response.Message(401, "401 Unauthorized") : GraphQlAnswer(user, r);
            }

            var api = r.Segments.SkipWhile(s => s != "v4").Skip(1).ToArray();
            if (api.Length == 1 && api[0] == "version")
            {
                return user is null ? Response.Message(401, "401 Unauthorized") : Response.Json(new JsonObject { ["version"] = "19.5.1-ee", ["revision"] = "fake" });
            }

            if (user is null)
            {
                return Response.Message(401, "401 Unauthorized");
            }

            return Rest(user, r, api);
        }
    }

    /// <summary>The account a request signs in as: its token (PRIVATE-TOKEN, or a bearer), or its session cookie.</summary>
    private User? Signed(Request r)
    {
        var token = r.Headers.GetValueOrDefault("PRIVATE-TOKEN")
            ?? (r.Headers.GetValueOrDefault("Authorization") is { } auth && auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : null);
        if (token is not null)
        {
            return _users.FirstOrDefault(u => u.Tokens.Contains(token));
        }

        return SessionUser(r);
    }

    private Response Rest(User user, Request r, string[] api)
    {
        switch (api)
        {
            case ["user"]:
                return Response.Json(UserJson(user));
            case ["groups"]:
                var groups = _groups.Where(g => r.Q("min_access_level") is null ? Sees(user, g) : Lineage(g).Any(x => x.Members.Contains(user.Id))).OrderBy(g => g.Id);
                return Paged(r, groups.Select(GroupJson));
            case ["groups", var id]:
                return FindGroup(user, id) is { } group ? Response.Json(GroupJson(group)) : Response.NotFound("404 Group Not Found");
            case ["groups", var id, "subgroups"]:
                return FindGroup(user, id) is { } parent
                    ? Paged(r, _groups.Where(g => g.Parent == parent).OrderBy(g => g.Path, StringComparer.Ordinal).Select(GroupJson))
                    : Response.NotFound("404 Group Not Found");
            case ["groups", var id, "wikis"]:
                if (FindGroup(user, id) is not { } wikiGroup)
                {
                    return Response.NotFound("404 Group Not Found");
                }

                return Premium ? Response.Json(WikiJson(wikiGroup.WikiPages, r)) : Response.NotFound("404 Not Found");
            case ["groups", var id, "projects"]:
                if (FindGroup(user, id) is not { } owner)
                {
                    return Response.NotFound("404 Group Not Found");
                }

                var deep = r.Q("include_subgroups") == "true";
                return Paged(r, _projects.Where(p => p.Group is { } g && (g == owner || deep && Lineage(g).Contains(owner))).OrderBy(p => p.Path, StringComparer.Ordinal).Select(ProjectJson));
            case ["projects"]:
                return Paged(r, _projects.Where(p => Sees(user, p)).OrderBy(p => p.Id).Select(ProjectJson));
            case ["projects", var id, .. var rest]:
                if (FindProject(user, id) is not { } project)
                {
                    return Response.NotFound("404 Project Not Found");
                }

                return rest.Length == 0 ? Response.Json(ProjectJson(project)) : ProjectRest(user, project, r, rest);
            default:
                return Response.NotFound("404 Not Found");
        }
    }

    private Response ProjectRest(User user, Project project, Request r, string[] rest)
    {
        switch (rest)
        {
            case ["repository", "branches"]:
                return Paged(r, project.Branches.OrderBy(b => b.Key, StringComparer.Ordinal).Select(b => RefJson(project, b.Key, b.Value, b.Key == project.DefaultBranch)));
            case ["repository", "branches", var name]:
                return project.Branches.TryGetValue(name, out var head) ? Response.Json(RefJson(project, name, head, name == project.DefaultBranch)) : Response.NotFound("404 Branch Not Found");
            case ["repository", "tags"]:
                return Paged(r, project.Tags.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => RefJson(project, t.Key, t.Value, false)));
            case ["repository", "tree"]:
                return Tree(project, r);
            case ["repository", "blobs", var sha, "raw"]:
                foreach (var commit in project.Commits.Values)
                {
                    foreach (var file in commit.Files.Keys)
                    {
                        if (Sha1(commit.Blob(file)) == sha)
                        {
                            return new Response(200, commit.Blob(file), "text/plain");
                        }
                    }
                }

                return Response.NotFound("404 Blob Not Found");
            case ["repository", "files", var file, "raw"]:
            {
                if (CommitOf(project, r.Q("ref")) is not { } commit || !commit.Files.ContainsKey(file))
                {
                    return Response.NotFound("404 File Not Found");
                }

                var bytes = r.Q("lfs") == "true" && commit.Lfs.Contains(file) ? commit.Files[file] : commit.Blob(file);
                return new Response(200, bytes, "text/plain");
            }

            case ["repository", "files", var file] when r.Method == "HEAD":
            {
                if (CommitOf(project, r.Q("ref")) is not { } commit || !commit.Files.ContainsKey(file))
                {
                    return Response.NotFound("404 File Not Found");
                }

                return new Response(200, [], "application/json", new() { ["X-Gitlab-Size"] = commit.Blob(file).Length.ToString(CultureInfo.InvariantCulture) });
            }

            default:
                return ProjectFeatures(user, project, r, rest);
        }
    }

    /// <summary>The repository's tree, a page at a time (keyset: <c>page_token</c> is the entry's index) or recursive.</summary>
    private Response Tree(Project project, Request r)
    {
        if (CommitOf(project, r.Q("ref")) is not { } commit)
        {
            return Response.NotFound("404 Tree Not Found");
        }

        var folder = r.Q("path") ?? "";
        if (!commit.IsFolder(folder))
        {
            return Response.NotFound("404 Tree Not Found");
        }

        var entries = r.Q("recursive") == "true" ? Recursive(commit, folder) : commit.Tree(folder).Select(e => (Path: (folder.Length == 0 ? "" : folder + "/") + e.Name, e.Name, e.Type, e.Id, e.Mode)).ToList();
        var json = entries.Select(e => (JsonNode)new JsonObject { ["id"] = e.Id, ["name"] = e.Name, ["type"] = e.Type, ["path"] = e.Path, ["mode"] = e.Mode });
        if (r.Q("pagination") != "keyset")
        {
            return Paged(r, json);
        }

        var perPage = int.TryParse(r.Q("per_page"), out var pp) ? pp : 20;
        var start = int.TryParse(r.Q("page_token"), out var t) ? t : 0;
        var page = json.Skip(start).Take(perPage).ToList();
        var headers = new Dictionary<string, string>();
        if (start + perPage < entries.Count)
        {
            headers["Link"] = $"<{External(With(r.Url, "page_token", (start + perPage).ToString(CultureInfo.InvariantCulture)))}>; rel=\"next\"";
        }

        return Response.Json(new JsonArray(page.ToArray()), 200, headers);
    }

    private static List<(string Path, string Name, string Type, string Id, string Mode)> Recursive(Commit commit, string folder)
    {
        var all = new List<(string, string, string, string, string)>();
        foreach (var entry in commit.Tree(folder))
        {
            var path = (folder.Length == 0 ? "" : folder + "/") + entry.Name;
            all.Add((path, entry.Name, entry.Type, entry.Id, entry.Mode));
            if (entry.Type == "tree")
            {
                all.AddRange(Recursive(commit, path));
            }
        }

        return all;
    }

    private Response GraphQlAnswer(User user, Request r)
    {
        if (!GraphQl)
        {
            return Response.NotFound();
        }

        using var doc = JsonDocument.Parse(r.Body);
        var query = doc.RootElement.Str("query") ?? "";
        var variables = doc.RootElement.Obj("variables");
        if (!query.Contains("blobs", StringComparison.Ordinal))
        {
            return Response.Json(new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = "fake: unknown query" }) });
        }

        var project = _projects.FirstOrDefault(p => p.FullPath == variables.Str("project") && Sees(user, p));
        if (project is null || CommitOf(project, variables.Str("ref")) is not { } commit)
        {
            return Response.Json(new JsonObject { ["data"] = new JsonObject { ["project"] = null } });
        }

        var nodes = new JsonArray();
        foreach (var path in variables.Arr("paths").Select(p => p.GetString()!).Take(100))
        {
            if (commit.Files.TryGetValue(path, out var bytes))
            {
                var lfs = commit.Lfs.Contains(path);
                nodes.Add(new JsonObject
                {
                    ["path"] = path,
                    ["size"] = commit.Blob(path).Length.ToString(CultureInfo.InvariantCulture),
                    ["rawSize"] = bytes.Length.ToString(CultureInfo.InvariantCulture),
                    ["lfsOid"] = lfs ? Convert.ToHexStringLower(SHA256.HashData(bytes)) : null,
                });
            }
        }

        return Response.Json(new JsonObject { ["data"] = new JsonObject { ["project"] = new JsonObject { ["repository"] = new JsonObject { ["blobs"] = new JsonObject { ["nodes"] = nodes } } } } });
    }

    private static Commit? CommitOf(Project project, string? reference)
    {
        if (reference is null)
        {
            return project.Branches.TryGetValue(project.DefaultBranch, out var head) ? project.Commits[head] : null;
        }

        if (project.Commits.TryGetValue(reference, out var commit))
        {
            return commit;
        }

        return project.Branches.TryGetValue(reference, out var b) ? project.Commits[b] : project.Tags.TryGetValue(reference, out var t) ? project.Commits[t] : null;
    }

    private Group? FindGroup(User user, string id)
    {
        var group = long.TryParse(id, out var n) ? _groups.FirstOrDefault(g => g.Id == n) : _groups.FirstOrDefault(g => g.FullPath == id);
        return group is not null && (Sees(user, group) || _projects.Any(p => p.Group is { } pg && Lineage(pg).Contains(group) && Sees(user, p))) ? group : null;
    }

    private Project? FindProject(User user, string id)
    {
        var project = long.TryParse(id, out var n) ? _projects.FirstOrDefault(p => p.Id == n) : _projects.FirstOrDefault(p => p.FullPath == id);
        return project is not null && Sees(user, project) ? project : null;
    }

    /// <summary>A listing a page at a time: <c>page</c> and <c>per_page</c>, the Link header's next page, X-Total; keyset (<c>id_after</c>) for projects.</summary>
    private Response Paged(Request r, IEnumerable<JsonNode> all)
    {
        var items = all.ToList();
        var perPage = int.TryParse(r.Q("per_page"), out var pp) ? Math.Clamp(pp, 1, 100) : 20;
        int start;
        string next;
        if (r.Q("pagination") == "keyset")
        {
            var after = long.TryParse(r.Q("id_after"), out var a) ? a : long.MinValue;
            start = items.FindIndex(i => i["id"]!.GetValue<long>() > after);
            start = start < 0 ? items.Count : start;
            next = start + perPage < items.Count ? External(With(r.Url, "id_after", items[start + perPage - 1]["id"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture))) : "";
        }
        else
        {
            var page = int.TryParse(r.Q("page"), out var p) ? Math.Max(1, p) : 1;
            start = (page - 1) * perPage;
            next = start + perPage < items.Count ? External(With(r.Url, "page", (page + 1).ToString(CultureInfo.InvariantCulture))) : "";
        }

        var headers = new Dictionary<string, string> { ["X-Total"] = items.Count.ToString(CultureInfo.InvariantCulture) };
        if (next.Length > 0)
        {
            headers["Link"] = $"<{next}>; rel=\"next\", <{r.Url.AbsoluteUri}>; rel=\"first\"";
        }

        return Response.Json(new JsonArray(items.Skip(start).Take(perPage).Select(i => i.DeepClone()).ToArray()), 200, headers);
    }

    /// <summary>An address as GitLab writes it in a Link header: at its external URL, whatever address the request came to.</summary>
    private string External(Uri url) => ExternalUrl + url.AbsoluteUri[Server.Length..];

    private static Uri With(Uri url, string name, string value)
    {
        var query = (url.Query.Length > 1 ? url.Query[1..] : "").Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith(name + "=", StringComparison.Ordinal)).Append($"{name}={Uri.EscapeDataString(value)}");
        return new Uri($"{url.GetLeftPart(UriPartial.Path)}?{string.Join('&', query)}");
    }

    /// <summary>
    /// The web pages: a wiki's attachments (<c>/&lt;project&gt;/-/wikis/uploads/&lt;hash&gt;/&lt;file&gt;</c>, a group's under
    /// <c>/groups/</c>), which GitLab serves to a browser session only and sends anything else to the sign-in page for.
    /// </summary>
    private Response? AnswerPages(Request r, string path)
    {
        var marker = path.IndexOf("/-/wikis/uploads/", StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        var owner = Uri.UnescapeDataString(path[1..marker]);
        var file = Uri.UnescapeDataString(path[(marker + "/-/wikis/uploads/".Length)..]);
        if (SessionUser(r) is not { } user)
        {
            return new Response(302, Encoding.UTF8.GetBytes("<html>sign in</html>"), "text/html", new() { ["Location"] = $"{Server}/users/sign_in" });
        }

        var uploads = owner.StartsWith("groups/", StringComparison.Ordinal)
            ? _groups.FirstOrDefault(g => g.FullPath == owner["groups/".Length..] && Sees(user, g))?.WikiUploads
            : _projects.FirstOrDefault(p => p.FullPath == owner && Sees(user, p))?.WikiUploads;
        return uploads is not null && uploads.TryGetValue(file, out var bytes)
            ? new Response(200, bytes, "application/octet-stream", new() { ["Content-Length"] = bytes.Length.ToString(CultureInfo.InvariantCulture) })
            : new Response(404, Encoding.UTF8.GetBytes("<html>not found</html>"), "text/html");
    }

    /// <summary>The account a browser session's cookie signs in as: none yet.</summary>
    private static User? SessionUser(Request r)
    {
        _ = r;
        return null;
    }

    /// <summary>A project's features beyond its repository: its wiki.</summary>
    private Response ProjectFeatures(User user, Project project, Request r, string[] rest)
    {
        _ = user;
        switch (rest)
        {
            case ["wikis"]:
                return project.Wiki ? Response.Json(WikiJson(project.WikiPages, r)) : Response.Message(403, "403 Forbidden");
            default:
                return Tickets(project, r, rest) ?? PipelinesAndReleases(project, r, rest) ?? Response.NotFound("404 Not Found");
        }
    }

    private static JsonArray WikiJson(List<WikiPage> pages, Request r)
        => new(pages.Select(p =>
        {
            var page = new JsonObject { ["slug"] = p.Slug, ["title"] = p.Title, ["format"] = p.Format, ["encoding"] = "UTF-8" };
            if (r.Q("with_content") is "1" or "true")
            {
                page["content"] = p.Content;
            }

            return (JsonNode)page;
        }).ToArray());

    /// <summary>A wiki page as the fake keeps it.</summary>
    public sealed record WikiPage(string Slug, string Title, string Format, string Content);

    /// <summary>Whether group wikis answer (GitLab Premium); on Free they are 404.</summary>
    public bool Premium { get; set; }

    private static JsonObject UserJson(User u) => new() { ["id"] = u.Id, ["username"] = u.Username, ["name"] = u.Name, ["state"] = "active" };

    private static JsonNode GroupJson(Group g) => new JsonObject
    {
        ["id"] = g.Id,
        ["path"] = g.Path,
        ["name"] = g.Name,
        ["full_path"] = g.FullPath,
        ["parent_id"] = g.Parent?.Id,
        ["created_at"] = "2026-01-01T00:00:00Z",
    };

    private JsonNode ProjectJson(Project p) => new JsonObject
    {
        ["id"] = p.Id,
        ["path"] = p.Path,
        ["name"] = p.Name,
        ["path_with_namespace"] = p.FullPath,
        ["namespace"] = new JsonObject { ["full_path"] = p.NamespacePath, ["kind"] = p.Group is null ? "user" : "group" },
        ["default_branch"] = p.EmptyRepo ? null : p.DefaultBranch,
        ["empty_repo"] = p.EmptyRepo,
        ["created_at"] = p.Created.ToString("O", CultureInfo.InvariantCulture),
        ["last_activity_at"] = p.Created.AddDays(30).ToString("O", CultureInfo.InvariantCulture),
        ["web_url"] = $"{Server}/{p.FullPath}",
        ["repository_access_level"] = "enabled",
        ["issues_access_level"] = p.Issues ? "enabled" : "disabled",
        ["merge_requests_access_level"] = p.MergeRequests ? "enabled" : "disabled",
        ["wiki_access_level"] = p.Wiki ? "enabled" : "disabled",
        ["builds_access_level"] = p.Pipelines ? "enabled" : "disabled",
        ["releases_access_level"] = "enabled",
    };

    private static JsonNode RefJson(Project p, string name, string commit, bool isDefault) => new JsonObject
    {
        ["name"] = name,
        ["default"] = isDefault,
        ["commit"] = new JsonObject { ["id"] = commit, ["committed_date"] = p.Commits[commit].When.ToString("O", CultureInfo.InvariantCulture) },
    };

    public static string Sha1(string text) => Sha1(Encoding.UTF8.GetBytes(text));

    public static string Sha1(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));

    private sealed class InProcessHandler(FakeGitLab fake) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
        {
            var url = message.RequestUri!;
            if (!url.GetLeftPart(UriPartial.Authority).Equals(fake.Origin, StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException($"No such host is known. ({url.Host})");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in message.Headers)
            {
                headers[header.Key] = string.Join(header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ? "; " : ", ", header.Value);
            }

            byte[] body = [];
            if (message.Content is { } content)
            {
                foreach (var header in content.Headers)
                {
                    headers[header.Key] = string.Join(", ", header.Value);
                }

                body = await content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }

            var raw = url.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
            var segments = raw.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
            var query = (url.Query.Length > 1 ? url.Query[1..] : "").Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "", StringComparer.Ordinal);
            var answer = fake.Answer(new Request(message.Method.Method, url, segments, query, headers, body));
            var response = new HttpResponseMessage((HttpStatusCode)answer.Status) { RequestMessage = message, Content = new ByteArrayContent(message.Method == HttpMethod.Head ? [] : answer.Body) };
            if (answer.ContentType is not null)
            {
                response.Content.Headers.TryAddWithoutValidation("Content-Type", answer.ContentType);
            }

            foreach (var (name, value) in answer.Headers ?? [])
            {
                if (!response.Headers.TryAddWithoutValidation(name, value))
                {
                    response.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }

            return response;
        }
    }
}
