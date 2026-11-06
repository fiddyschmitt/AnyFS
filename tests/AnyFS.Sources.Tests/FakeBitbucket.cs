using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AnyFS.Sources.Tests;

/// <summary>
/// A stand-in for a Bitbucket Data Center server (spec 7.11): projects (each visible to everyone or to some accounts),
/// repositories whose commits are snapshots of files, branches and tags pointing at them, and pull requests with their
/// activities and a diff; answered as Bitbucket's REST API 1.0 answers: paged listings (<c>start</c>, <c>limit</c>,
/// <c>isLastPage</c>, <c>nextPageStart</c>), the browse and raw calls at a commit, the commit call, pull requests with
/// their activities and <c>.diff</c>, the users call, and the application properties the session asks who it is through
/// (the account in <c>X-AUSERNAME</c>, as Seraph puts it on every answer).
/// </summary>
internal sealed class FakeBitbucket(string origin = "https://bitbucket.fake.test", string root = "/bitbucket") : FakeAtlassian(origin, root, "stash", "9.4.2")
{
    private readonly object _gate = new();
    private long _nextId = 100;

    public List<Project> Projects { get; } = [];

    /// <summary>The most entries a listing gives a page when asked for more (Bitbucket's own cap is a thousand); lower in a test of the paging.</summary>
    public int PageLimit { get; set; } = 1000;

    protected override string WhoPath => "rest/api/1.0/application-properties";

    protected override JsonNode Who(Account? user) => new JsonObject { ["version"] = Version, ["buildNumber"] = "9004002", ["buildDate"] = "1761000000000", ["displayName"] = "Bitbucket" };

    // --- the model -------------------------------------------------------------------------------------------------------

    public sealed class Project(long id, string key, string name)
    {
        public long Id { get; } = id;

        public string Key { get; } = key;

        public string Name { get; set; } = name;

        public string? Description { get; set; }

        public bool Public { get; set; }

        /// <summary>The accounts that may see it; null for everyone.</summary>
        public HashSet<string>? VisibleTo { get; set; }

        public List<Repository> Repositories { get; } = [];

        public bool Personal => Key.StartsWith('~');
    }

    public sealed class Repository(long id, string slug, string name, Project project)
    {
        public long Id { get; } = id;

        public string Slug { get; } = slug;

        public string Name { get; set; } = name;

        public Project Project { get; } = project;

        public string? Description { get; set; }

        public string State { get; set; } = "AVAILABLE";

        public Dictionary<string, Commit> Commits { get; } = new(StringComparer.Ordinal);

        /// <summary>Branch name → commit id.</summary>
        public Dictionary<string, string> Branches { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);

        public string? DefaultBranch { get; set; }

        public List<PullRequest> PullRequests { get; } = [];

        /// <summary>Paths that are submodules at every commit (a folder with nothing in it to the source).</summary>
        public HashSet<string> Submodules { get; } = new(StringComparer.Ordinal);

        /// <summary>A commit on a branch with these files (a snapshot of the whole tree), the branch moved to it; the first commit makes the branch the default.</summary>
        public string Commit(string branch, Dictionary<string, byte[]> files, DateTimeOffset when, string author = "tester", string message = "A commit")
        {
            var text = string.Join('\n', files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Key + ":" + Convert.ToHexString(SHA1.HashData(f.Value)))) + "|" + when.ToUnixTimeMilliseconds();
            var id = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            Commits[id] = new Commit(id, message, author, when, new Dictionary<string, byte[]>(files, StringComparer.Ordinal));
            Branches[branch] = id;
            DefaultBranch ??= branch;
            return id;
        }
    }

    public sealed record Commit(string Id, string Message, string Author, DateTimeOffset When, Dictionary<string, byte[]> Files);

    public sealed class PullRequest(long id, string title, string author, string fromBranch, string toBranch)
    {
        public long Id { get; } = id;

        public long Version { get; set; } = 1;

        public string Title { get; set; } = title;

        public string? Description { get; set; }

        public string State { get; set; } = "OPEN";

        public string Author { get; } = author;

        public string FromBranch { get; } = fromBranch;

        public string ToBranch { get; } = toBranch;

        public DateTimeOffset Created { get; set; } = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Updated { get; set; } = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

        public DateTimeOffset? Closed { get; set; }

        public List<(string Name, string Status)> Reviewers { get; } = [];

        public List<Activity> Activities { get; } = [];

        /// <summary>The diff as the .diff call gives it.</summary>
        public string Diff { get; set; } = "";
    }

    public sealed record Activity(long Id, DateTimeOffset Created, string User, string Action, Comment? Comment, string? AnchorPath = null, int? AnchorLine = null);

    public sealed record Comment(long Id, string Author, DateTimeOffset Created, string Text, List<Comment> Replies);

    public Project AddProject(string key, string name, params string[] visibleTo)
    {
        lock (_gate)
        {
            var project = new Project(++_nextId, key, name) { VisibleTo = visibleTo.Length == 0 ? null : [.. visibleTo] };
            Projects.Add(project);
            return project;
        }
    }

    public Repository AddRepository(Project project, string slug, string name)
    {
        lock (_gate)
        {
            var repository = new Repository(++_nextId, slug, name, project);
            project.Repositories.Add(repository);
            return repository;
        }
    }

    public PullRequest AddPullRequest(Repository repository, string title, string author, string fromBranch, string toBranch, Action<PullRequest>? fill = null)
    {
        lock (_gate)
        {
            var pr = new PullRequest(repository.PullRequests.Count + 1, title, author, fromBranch, toBranch);
            fill?.Invoke(pr);
            pr.Activities.Add(new Activity(++_nextId, pr.Created, author, "OPENED", null));
            repository.PullRequests.Add(pr);
            return pr;
        }
    }

    public Comment AddComment(PullRequest pr, string author, string text, DateTimeOffset when, string? path = null, int? line = null)
    {
        lock (_gate)
        {
            var comment = new Comment(++_nextId, author, when, text, []);
            pr.Activities.Add(new Activity(++_nextId, when, author, "COMMENTED", comment, path, line));
            pr.Version++;
            pr.Updated = when;
            return comment;
        }
    }

    public Comment Reply(PullRequest pr, Comment to, string author, string text, DateTimeOffset when)
    {
        lock (_gate)
        {
            var reply = new Comment(++_nextId, author, when, text, []);
            to.Replies.Add(reply);
            pr.Version++;
            pr.Updated = when;
            return reply;
        }
    }

    public void Approve(PullRequest pr, string reviewer, DateTimeOffset when)
    {
        lock (_gate)
        {
            pr.Reviewers.RemoveAll(r => r.Name == reviewer);
            pr.Reviewers.Add((reviewer, "APPROVED"));
            pr.Activities.Add(new Activity(++_nextId, when, reviewer, "APPROVED", null));
            pr.Version++;
            pr.Updated = when;
        }
    }

    public void Merge(PullRequest pr, string by, DateTimeOffset when)
    {
        lock (_gate)
        {
            pr.State = "MERGED";
            pr.Closed = when;
            pr.Updated = when;
            pr.Version++;
            pr.Activities.Add(new Activity(++_nextId, when, by, "MERGED", null));
        }
    }

    public Repository RepositoryAt(string projectKey, string slug) => Projects.Single(p => p.Key == projectKey).Repositories.Single(r => r.Slug == slug);

    // --- answering -------------------------------------------------------------------------------------------------------

    protected override Response AnswerProduct(Request request, string path, Account? user)
    {
        if (Unauthenticated(user) is { } refused)
        {
            return refused;
        }

        var s = path.Split('/');
        lock (_gate)
        {
            if (s is ["rest", "api", "1.0" or "latest", "users", var slug])
            {
                var account = Accounts.FirstOrDefault(a => a.Name.Equals(slug, StringComparison.OrdinalIgnoreCase));
                return account is null ? NotFound($"User {slug} does not exist.") : Response.Json(UserJson(account.Name));
            }

            if (s is ["rest", "api", "1.0" or "latest", "projects"])
            {
                return Paged(request, Projects.Where(p => !p.Personal && CanSee(user, p)).OrderBy(p => p.Key, StringComparer.Ordinal).Select(ProjectJson));
            }

            if (s.Length < 5 || s[0] != "rest" || s[1] != "api" || s[2] is not ("1.0" or "latest") || s[3] != "projects")
            {
                return NotFound();
            }

            var project = Projects.FirstOrDefault(p => p.Key.Equals(s[4], StringComparison.OrdinalIgnoreCase) && CanSee(user, p));
            if (project is null)
            {
                return NotFound($"Project {s[4]} does not exist.");
            }

            var rest = s[5..];
            if (rest.Length == 0)
            {
                return Response.Json(ProjectJson(project));
            }

            if (rest[0] != "repos")
            {
                return NotFound();
            }

            if (rest.Length == 1)
            {
                return Paged(request, project.Repositories.OrderBy(r => r.Slug, StringComparer.Ordinal).Select(RepositoryJson));
            }

            var repository = project.Repositories.FirstOrDefault(r => r.Slug.Equals(rest[1], StringComparison.OrdinalIgnoreCase));
            if (repository is null)
            {
                return NotFound($"Repository {project.Key}/{rest[1]} does not exist.");
            }

            return RepositoryAnswer(request, repository, rest[2..]);
        }
    }

    private Response RepositoryAnswer(Request request, Repository repository, string[] rest)
    {
        switch (rest)
        {
            case []:
                return Response.Json(RepositoryJson(repository));
            case ["branches"]:
                return Paged(request, repository.Branches.OrderBy(b => b.Key, StringComparer.Ordinal).Select(b => RefJson("refs/heads/" + b.Key, b.Key, b.Value, "BRANCH", b.Key == repository.DefaultBranch)));
            case ["tags"]:
                return Paged(request, repository.Tags.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => RefJson("refs/tags/" + t.Key, t.Key, t.Value, "TAG", false)));
            case ["commits", var id]:
                return repository.Commits.TryGetValue(id, out var commit) ? Response.Json(CommitJson(commit)) : NotFound($"Commit '{id}' does not exist in repository '{repository.Slug}'.");
            case ["pull-requests"]:
                var state = request.Q("state") ?? "OPEN";
                var requests = repository.PullRequests.Where(pr => state == "ALL" || pr.State.Equals(state, StringComparison.OrdinalIgnoreCase)).OrderByDescending(pr => pr.Id);
                return Paged(request, requests.Select(pr => PullRequestJson(repository, pr)));
            case ["pull-requests", var number] when number.EndsWith(".diff", StringComparison.Ordinal) && long.TryParse(number[..^5], out var diffId):
                return repository.PullRequests.FirstOrDefault(pr => pr.Id == diffId) is { } diffOf ? new Response(200, Encoding.UTF8.GetBytes(diffOf.Diff), "text/plain") : NotFound();
            case ["pull-requests", var number] when long.TryParse(number, out var prId):
                return repository.PullRequests.FirstOrDefault(pr => pr.Id == prId) is { } pr1 ? Response.Json(PullRequestJson(repository, pr1)) : NotFound($"Pull request {number} does not exist in {repository.Slug}.");
            case ["pull-requests", var number, "activities"] when long.TryParse(number, out var actId):
                return repository.PullRequests.FirstOrDefault(pr => pr.Id == actId) is { } pr2 ? Paged(request, pr2.Activities.OrderByDescending(a => a.Created).Select(ActivityJson)) : NotFound();
        }

        if (rest.Length >= 1 && rest[0] is "browse" or "raw")
        {
            var at = request.Q("at");
            var commit = at is not null ? (repository.Commits.TryGetValue(at, out var c) ? c : repository.Branches.TryGetValue(at, out var sha) && repository.Commits.TryGetValue(sha, out var c2) ? c2 : null)
                : repository.DefaultBranch is { } d && repository.Commits.TryGetValue(repository.Branches[d], out var c3) ? c3 : null;
            if (commit is null)
            {
                return NotFound($"Commit '{at}' does not exist in repository '{repository.Slug}'.");
            }

            var filePath = string.Join('/', rest[1..]);
            if (rest[0] == "raw")
            {
                return commit.Files.TryGetValue(filePath, out var bytes) ? Bytes(request, bytes) : NotFound($"The path \"{filePath}\" does not exist at revision \"{commit.Id}\"");
            }

            return Browse(request, repository, commit, filePath);
        }

        return NotFound();
    }

    /// <summary>The browse call: a folder's children (paged), a file's lines, or 404.</summary>
    private Response Browse(Request request, Repository repository, Commit commit, string path)
    {
        if (path.Length > 0 && commit.Files.TryGetValue(path, out var file))
        {
            return Response.Json(new JsonObject
            {
                ["lines"] = new JsonArray(Encoding.UTF8.GetString(file).Split('\n').Select(l => (JsonNode)new JsonObject { ["text"] = l }).ToArray()),
                ["start"] = 0,
                ["size"] = 1,
                ["isLastPage"] = true,
            });
        }

        var prefix = path.Length == 0 ? "" : path + "/";
        var children = new SortedDictionary<string, (string Kind, long Size, string? Id)>(StringComparer.Ordinal);
        foreach (var (full, bytes) in commit.Files)
        {
            if (!full.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = full[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                children[rest] = ("FILE", bytes.Length, Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant());
            }
            else
            {
                var dir = rest[..slash];
                children.TryAdd(dir, (repository.Submodules.Contains(prefix + dir) ? "SUBMODULE" : "DIRECTORY", 0, null));
            }
        }

        if (children.Count == 0 && path.Length > 0)
        {
            return NotFound($"The path \"{path}\" does not exist at revision \"{commit.Id}\"");
        }

        var values = children.Select(c => (JsonNode)new JsonObject
        {
            ["path"] = new JsonObject { ["components"] = new JsonArray((JsonNode)c.Key), ["name"] = c.Key, ["toString"] = c.Key, ["extension"] = c.Key.Contains('.') ? c.Key[(c.Key.LastIndexOf('.') + 1)..] : null },
            ["type"] = c.Value.Kind,
            ["size"] = c.Value.Kind == "FILE" ? c.Value.Size : null,
            ["contentId"] = c.Value.Id,
            ["node"] = c.Value.Id,
        }).ToList();
        var page = Page(request, values);
        return Response.Json(new JsonObject
        {
            ["path"] = new JsonObject { ["components"] = new JsonArray(path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(p => (JsonNode)p).ToArray()), ["name"] = path.Split('/')[^1], ["toString"] = path },
            ["revision"] = commit.Id,
            ["children"] = page,
        });
    }

    /// <summary>A page of a listing as Bitbucket gives one: start and limit from the query, isLastPage and nextPageStart.</summary>
    private Response Paged(Request request, IEnumerable<JsonNode> items) => Response.Json(Page(request, items.ToList()));

    private JsonObject Page(Request request, List<JsonNode> items)
    {
        var start = request.QInt("start", 0);
        var limit = Math.Min(request.QInt("limit", 25), PageLimit);
        var page = items.Skip(start).Take(limit).ToList();
        var last = start + page.Count >= items.Count;
        var json = new JsonObject
        {
            ["size"] = page.Count,
            ["limit"] = limit,
            ["isLastPage"] = last,
            ["values"] = new JsonArray(page.ToArray()),
            ["start"] = start,
        };
        if (!last)
        {
            json["nextPageStart"] = start + page.Count;
        }

        return json;
    }

    private static bool CanSee(Account? user, Project project) => project.Public || project.VisibleTo is null || user is not null && project.VisibleTo.Contains(user.Name);

    private static Response NotFound(string message = "Not Found") => Response.Json(new JsonObject { ["errors"] = new JsonArray((JsonNode)new JsonObject { ["context"] = null, ["message"] = message, ["exceptionName"] = "com.atlassian.bitbucket.NoSuchEntityException" }) }, 404);

    private JsonObject UserJson(string name)
    {
        var account = Accounts.FirstOrDefault(a => a.Name == name);
        return new JsonObject { ["name"] = name, ["emailAddress"] = name + "@fake.test", ["id"] = Math.Abs(name.GetHashCode(StringComparison.Ordinal)) % 1000, ["displayName"] = account?.Display ?? name, ["active"] = true, ["slug"] = name, ["type"] = "NORMAL" };
    }

    private JsonObject ProjectJson(Project p) => new()
    {
        ["key"] = p.Key,
        ["id"] = p.Id,
        ["name"] = p.Name,
        ["description"] = p.Description,
        ["public"] = p.Public,
        ["type"] = p.Personal ? "PERSONAL" : "NORMAL",
        ["links"] = new JsonObject { ["self"] = new JsonArray((JsonNode)new JsonObject { ["href"] = $"{Server}/projects/{p.Key}" }) },
    };

    private JsonObject RepositoryJson(Repository r) => new()
    {
        ["slug"] = r.Slug,
        ["id"] = r.Id,
        ["name"] = r.Name,
        ["description"] = r.Description,
        ["hierarchyId"] = r.Id.ToString("x", CultureInfo.InvariantCulture),
        ["scmId"] = "git",
        ["state"] = r.State,
        ["statusMessage"] = "Available",
        ["forkable"] = true,
        ["project"] = ProjectJson(r.Project),
        ["public"] = r.Project.Public,
        ["links"] = new JsonObject
        {
            ["clone"] = new JsonArray((JsonNode)new JsonObject { ["href"] = $"{Server}/scm/{r.Project.Key.ToLowerInvariant()}/{r.Slug}.git", ["name"] = "http" }),
            ["self"] = new JsonArray((JsonNode)new JsonObject { ["href"] = $"{Server}/projects/{r.Project.Key}/repos/{r.Slug}/browse" }),
        },
    };

    private static JsonObject RefJson(string id, string display, string commit, string type, bool isDefault) => new()
    {
        ["id"] = id,
        ["displayId"] = display,
        ["type"] = type,
        ["latestCommit"] = commit,
        ["latestChangeset"] = commit,
        ["isDefault"] = isDefault,
    };

    private JsonObject CommitJson(Commit c) => new()
    {
        ["id"] = c.Id,
        ["displayId"] = c.Id[..11],
        ["author"] = UserJson(c.Author),
        ["authorTimestamp"] = c.When.ToUnixTimeMilliseconds(),
        ["committer"] = UserJson(c.Author),
        ["committerTimestamp"] = c.When.ToUnixTimeMilliseconds(),
        ["message"] = c.Message,
        ["parents"] = new JsonArray(),
    };

    private JsonObject PullRequestJson(Repository r, PullRequest pr)
    {
        JsonObject Ref(string branch) => new()
        {
            ["id"] = "refs/heads/" + branch,
            ["displayId"] = branch,
            ["latestCommit"] = r.Branches.GetValueOrDefault(branch) ?? "",
            ["type"] = "BRANCH",
            ["repository"] = new JsonObject { ["slug"] = r.Slug, ["id"] = r.Id, ["name"] = r.Name, ["project"] = new JsonObject { ["key"] = r.Project.Key, ["id"] = r.Project.Id, ["name"] = r.Project.Name } },
        };
        var json = new JsonObject
        {
            ["id"] = pr.Id,
            ["version"] = pr.Version,
            ["title"] = pr.Title,
            ["description"] = pr.Description,
            ["state"] = pr.State,
            ["open"] = pr.State == "OPEN",
            ["closed"] = pr.State != "OPEN",
            ["createdDate"] = pr.Created.ToUnixTimeMilliseconds(),
            ["updatedDate"] = pr.Updated.ToUnixTimeMilliseconds(),
            ["fromRef"] = Ref(pr.FromBranch),
            ["toRef"] = Ref(pr.ToBranch),
            ["locked"] = false,
            ["author"] = new JsonObject { ["user"] = UserJson(pr.Author), ["role"] = "AUTHOR", ["approved"] = false, ["status"] = "UNAPPROVED" },
            ["reviewers"] = new JsonArray(pr.Reviewers.Select(rv => (JsonNode)new JsonObject { ["user"] = UserJson(rv.Name), ["role"] = "REVIEWER", ["approved"] = rv.Status == "APPROVED", ["status"] = rv.Status }).ToArray()),
            ["participants"] = new JsonArray(),
            ["links"] = new JsonObject { ["self"] = new JsonArray((JsonNode)new JsonObject { ["href"] = $"{Server}/projects/{r.Project.Key}/repos/{r.Slug}/pull-requests/{pr.Id}" }) },
        };
        if (pr.Closed is { } closed)
        {
            json["closedDate"] = closed.ToUnixTimeMilliseconds();
        }

        return json;
    }

    private JsonObject ActivityJson(Activity a)
    {
        var json = new JsonObject
        {
            ["id"] = a.Id,
            ["createdDate"] = a.Created.ToUnixTimeMilliseconds(),
            ["user"] = UserJson(a.User),
            ["action"] = a.Action,
        };
        if (a.Comment is { } comment)
        {
            json["commentAction"] = "ADDED";
            json["comment"] = CommentJson(comment);
            if (a.AnchorPath is { } anchorPath)
            {
                json["commentAnchor"] = new JsonObject { ["path"] = anchorPath, ["line"] = a.AnchorLine, ["lineType"] = "ADDED", ["fileType"] = "TO", ["diffType"] = "EFFECTIVE" };
            }
        }

        return json;
    }

    private JsonObject CommentJson(Comment c) => new()
    {
        ["id"] = c.Id,
        ["version"] = 0,
        ["text"] = c.Text,
        ["author"] = UserJson(c.Author),
        ["createdDate"] = c.Created.ToUnixTimeMilliseconds(),
        ["updatedDate"] = c.Created.ToUnixTimeMilliseconds(),
        ["comments"] = new JsonArray(c.Replies.Select(r => (JsonNode)CommentJson(r)).ToArray()),
        ["severity"] = "NORMAL",
        ["state"] = "OPEN",
    };
}
