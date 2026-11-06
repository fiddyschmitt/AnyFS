using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace AnyFS.Sources.Tests;

/// <summary>
/// A stand-in for a Bamboo Data Center server (spec 7.12): projects (each visible to everyone or to some accounts), plans
/// with their jobs, plan branches, and builds with a result per job, each job's log and the artifacts it kept; answered as
/// Bamboo's REST API answers: paged lists (<c>start-index</c>, <c>max-result</c>; a list object with <c>size</c>), the
/// project, plan, branch and result calls with their expansions, a log download, and the pages Bamboo shows for an
/// artifact's folder (links to its files and folders) or serves for its file.
/// </summary>
internal sealed class FakeBamboo(string origin = "https://bamboo.fake.test", string root = "/bamboo") : FakeAtlassian(origin, root, "bamboo", "9.6.5")
{
    private readonly object _gate = new();

    public List<Project> Projects { get; } = [];

    /// <summary>The most entries a list gives a page when asked for more; lower in a test of the paging.</summary>
    public int PageLimit { get; set; } = 1000;

    protected override string WhoPath => "rest/api/latest/currentUser";

    protected override JsonNode Who(Account? user) => new JsonObject { ["name"] = user?.Name ?? "anonymous", ["fullName"] = user?.Display ?? "Anonymous", ["email"] = user is null ? null : user.Name + "@fake.test" };

    // --- the model -------------------------------------------------------------------------------------------------------

    public sealed class Project(string key, string name)
    {
        public string Key { get; } = key;

        public string Name { get; set; } = name;

        public string? Description { get; set; }

        /// <summary>The accounts that may see it; null for everyone.</summary>
        public HashSet<string>? VisibleTo { get; set; }

        public List<Plan> Plans { get; } = [];
    }

    /// <summary>A plan, or a plan branch (a plan with a key of its own, its jobs its plan's).</summary>
    public sealed class Plan(string key, string shortName, Project project, Plan? branchOf = null)
    {
        private readonly List<Job> _jobs = [];

        public string Key { get; } = key;

        public string ShortKey => Key[(Key.IndexOf('-') + 1)..];

        public string ShortName { get; set; } = shortName;

        public string Name => BranchOf is null ? $"{Project.Name} - {ShortName}" : $"{Project.Name} - {BranchOf.ShortName} - {ShortName}";

        public Project Project { get; } = project;

        public Plan? BranchOf { get; } = branchOf;

        public bool Enabled { get; set; } = true;

        public string? Description { get; set; }

        public List<Job> Jobs => BranchOf?.Jobs ?? _jobs;

        public List<Build> Builds { get; } = [];

        public List<Plan> Branches { get; } = [];
    }

    public sealed record Job(string ShortKey, string Name, string Stage);

    public sealed class Build(Plan plan, long number)
    {
        public Plan Plan { get; } = plan;

        public long Number { get; } = number;

        public string Key => $"{Plan.Key}-{Number}";

        public string State { get; set; } = "Successful";

        public string LifeCycle { get; set; } = "Finished";

        public DateTimeOffset Started { get; set; }

        public DateTimeOffset? Completed { get; set; }

        /// <summary>Why it ran, as Bamboo writes it: HTML with a link to the person.</summary>
        public string Reason { get; set; } = "";

        public long Passed { get; set; }

        public long Failed { get; set; }

        public long Skipped { get; set; }

        public long Quarantined { get; set; }

        public List<Change> Changes { get; } = [];

        public List<(string Key, string Url)> Issues { get; } = [];

        public List<JobRun> JobRuns { get; } = [];
    }

    /// <summary>One job's result in a build: its log and the artifacts it kept.</summary>
    public sealed class JobRun(Build build, Job job)
    {
        public Build Build { get; } = build;

        public Job Job { get; } = job;

        public string JobKey => $"{Build.Plan.Key}-{Job.ShortKey}";

        public string Key => $"{JobKey}-{Build.Number}";

        public string State { get; set; } = build.State;

        public string LifeCycle { get; set; } = build.LifeCycle;

        public DateTimeOffset? Started { get; set; } = build.Started;

        public DateTimeOffset? Completed { get; set; } = build.Completed;

        public byte[]? Log { get; set; }

        public List<Artifact> Artifacts { get; } = [];
    }

    /// <summary>An artifact: a folder of files (Bamboo shows a page of them), or one file its link points straight at.</summary>
    public sealed class Artifact(string name, bool singleFile = false)
    {
        public string Name { get; } = name;

        public bool SingleFile { get; } = singleFile;

        public bool Shared { get; set; }

        /// <summary>Path below the artifact → bytes.</summary>
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public long Size => Files.Values.Sum(f => (long)f.Length);
    }

    public sealed record Change(string Author, string ChangesetId, DateTimeOffset Date, string Comment, List<string> Files);

    public Project AddProject(string key, string name, params string[] visibleTo)
    {
        lock (_gate)
        {
            var project = new Project(key, name) { VisibleTo = visibleTo.Length == 0 ? null : [.. visibleTo] };
            Projects.Add(project);
            return project;
        }
    }

    public Plan AddPlan(Project project, string shortKey, string shortName)
    {
        lock (_gate)
        {
            var plan = new Plan($"{project.Key}-{shortKey}", shortName, project);
            project.Plans.Add(plan);
            return plan;
        }
    }

    public Job AddJob(Plan plan, string shortKey, string name, string stage = "Default Stage")
    {
        lock (_gate)
        {
            var job = new Job(shortKey, name, stage);
            plan.Jobs.Add(job);
            return job;
        }
    }

    /// <summary>A plan branch: a plan of its own, keyed as Bamboo keys them (the plan's key with a number after it).</summary>
    public Plan AddBranch(Plan plan, string name, int id)
    {
        lock (_gate)
        {
            var branch = new Plan(plan.Key + id.ToString(CultureInfo.InvariantCulture), name, plan.Project, plan);
            plan.Branches.Add(branch);
            return branch;
        }
    }

    /// <summary>A build of a plan, numbered next, with a result for each job named (every job when none is), each with a log.</summary>
    public Build AddBuild(Plan plan, string state, DateTimeOffset started, DateTimeOffset? completed, Action<Build>? fill = null, params string[] jobs)
    {
        lock (_gate)
        {
            var build = new Build(plan, plan.Builds.Count + 1) { State = state, Started = started, Completed = completed, Reason = "Manual run by <a href=\"" + Server + "/browse/user/tester\">Test Er</a>" };
            fill?.Invoke(build);
            foreach (var job in plan.Jobs.Where(j => jobs.Length == 0 || jobs.Contains(j.ShortKey)))
            {
                var run = new JobRun(build, job);
                run.Log = Encoding.UTF8.GetBytes($"simple\t{started:dd-MMM-yyyy HH:mm:ss}\tBuild {plan.Name} #{build.Number} ({run.Key}) started building on agent 1\n" + (completed is null ? "" : $"simple\t{completed:dd-MMM-yyyy HH:mm:ss}\tFinished building {run.Key}. {build.State}.\n"));
                build.JobRuns.Add(run);
            }

            plan.Builds.Add(build);
            return build;
        }
    }

    public Artifact AddArtifact(JobRun run, string name, bool singleFile = false)
    {
        lock (_gate)
        {
            var artifact = new Artifact(name, singleFile);
            run.Artifacts.Add(artifact);
            return artifact;
        }
    }

    public Plan PlanAt(string key) => AllPlans().Single(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public Build BuildAt(string planKey, long number) => PlanAt(planKey).Builds.Single(b => b.Number == number);

    private IEnumerable<Plan> AllPlans() => Projects.SelectMany(p => p.Plans).SelectMany(p => p.Branches.Prepend(p));

    // --- answering -------------------------------------------------------------------------------------------------------

    protected override Response AnswerProduct(Request request, string path, Account? user)
    {
        if (Unauthenticated(user) is { } refused)
        {
            return refused;
        }

        var s = path.Split('/');
        var expand = request.Q("expand") ?? "";
        lock (_gate)
        {
            switch (s)
            {
                case ["rest", "api", "latest", "info"]:
                    return Response.Json(new JsonObject { ["version"] = Version, ["edition"] = "Data Center", ["buildDate"] = "2026-09-01T00:00:00.000+00:00", ["buildNumber"] = "90605", ["state"] = "RUNNING" });
                case ["rest", "api", "latest", "project"]:
                    return Paged(request, "projects", "project", Projects.Where(p => CanSee(user, p)).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (JsonNode)ProjectJson(p)).ToList());
                case ["rest", "api", "latest", "project", var key]:
                    {
                        var project = Projects.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && CanSee(user, p));
                        if (project is null)
                        {
                            return NotFound($"No project could be found with key: {key}");
                        }

                        return expand.Contains("plans", StringComparison.Ordinal)
                            ? Paged(request, "plans", "plan", project.Plans.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (JsonNode)PlanJson(p)).ToList(), ProjectJson(project))
                            : Response.Json(ProjectJson(project));
                    }

                case ["rest", "api", "latest", "plan"]:
                    return Paged(request, "plans", "plan", Projects.Where(p => CanSee(user, p)).SelectMany(p => p.Plans).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (JsonNode)PlanJson(p)).ToList());
                case ["rest", "api", "latest", "plan", var key]:
                    return FindPlan(user, key) is { } plan ? Response.Json(PlanJson(plan)) : NotFound($"No plan could be found with key: {key}");
                case ["rest", "api", "latest", "plan", var key, "branch"]:
                    return FindPlan(user, key) is { } ofBranches
                        ? Paged(request, "branches", "branch", ofBranches.Branches.OrderBy(b => b.Key, StringComparer.Ordinal).Select(b => (JsonNode)BranchJson(b)).ToList())
                        : NotFound($"No plan could be found with key: {key}");
                case ["rest", "api", "latest", "result", var key]:
                    {
                        if (FindPlan(user, key) is { } ofResults)
                        {
                            return Paged(request, "results", "result", ofResults.Builds.OrderByDescending(b => b.Number).Select(b => (JsonNode)ResultJson(b, expand, full: false)).ToList());
                        }

                        return FindBuild(user, key) is { } build ? Response.Json(ResultJson(build, expand, full: true)) : NotFound($"No result could be found for key: {key}");
                    }

                case ["download", var jobKey, "build_logs", var file]:
                    {
                        var run = AllRuns(user).FirstOrDefault(r => r.JobKey.Equals(jobKey, StringComparison.OrdinalIgnoreCase) && file.Equals(r.Key + ".log", StringComparison.OrdinalIgnoreCase));
                        return run is null ? new Response(404, "<html>Not Found</html>"u8.ToArray(), "text/html") : Bytes(request, run.Log ?? [], "text/plain");
                    }

                case ["browse", var resultKey, "artifact", var jobShortKey, var artifactName, .. var rest]:
                    {
                        var run = AllRuns(user).FirstOrDefault(r => r.Key.Equals(resultKey, StringComparison.OrdinalIgnoreCase) && r.Job.ShortKey.Equals(jobShortKey, StringComparison.OrdinalIgnoreCase));
                        var artifact = run?.Artifacts.FirstOrDefault(a => a.Name.Equals(artifactName, StringComparison.OrdinalIgnoreCase));
                        return artifact is null ? new Response(404, "<html>Not Found</html>"u8.ToArray(), "text/html") : ArtifactAnswer(request, artifact, string.Join('/', rest));
                    }
            }

            return NotFound();
        }
    }

    /// <summary>An artifact's file, or the page Bamboo shows for a folder of it: links to what is in it.</summary>
    private static Response ArtifactAnswer(Request request, Artifact artifact, string filePath)
    {
        if (artifact.Files.TryGetValue(filePath, out var bytes))
        {
            return Bytes(request, bytes);
        }

        if (artifact.SingleFile)
        {
            return new Response(404, "<html>Not Found</html>"u8.ToArray(), "text/html");
        }

        var prefix = filePath.Length == 0 ? "" : filePath + "/";
        var entries = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var full in artifact.Files.Keys)
        {
            if (!full.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = full[prefix.Length..];
            var slash = rest.IndexOf('/');
            entries[slash < 0 ? rest : rest[..slash]] = slash >= 0;
        }

        if (entries.Count == 0)
        {
            return new Response(404, "<html>Not Found</html>"u8.ToArray(), "text/html");
        }

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><title>Index of /").Append(WebUtility.HtmlEncode(artifact.Name + "/" + prefix)).Append("</title></head><body><h1>Index of /")
            .Append(WebUtility.HtmlEncode(artifact.Name + "/" + prefix)).Append("</h1><table><tr><td><a href=\"../\">Parent Directory</a></td><td></td></tr>");
        foreach (var (name, directory) in entries)
        {
            sb.Append("<tr><td><a href=\"").Append(Uri.EscapeDataString(name)).Append(directory ? "/" : "").Append("\">").Append(WebUtility.HtmlEncode(name)).Append(directory ? "/" : "").Append("</a></td><td>")
                .Append(directory ? "-" : artifact.Files[prefix + name].Length.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>");
        }

        sb.Append("</table></body></html>");
        return Response.Html(sb.ToString());
    }

    private Plan? FindPlan(Account? user, string key) => AllPlans().FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && CanSee(user, p.Project));

    private Build? FindBuild(Account? user, string key)
    {
        var dash = key.LastIndexOf('-');
        if (dash <= 0 || !long.TryParse(key[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        return FindPlan(user, key[..dash])?.Builds.FirstOrDefault(b => b.Number == number);
    }

    private IEnumerable<JobRun> AllRuns(Account? user) => AllPlans().Where(p => CanSee(user, p.Project)).SelectMany(p => p.Builds).SelectMany(b => b.JobRuns);

    /// <summary>A page of a list as Bamboo gives one: the list object with its size, start-index and max-result, under its name, in <paramref name="wrap"/> or a body of its own.</summary>
    private Response Paged(Request request, string collection, string item, List<JsonNode> items, JsonObject? wrap = null)
    {
        var start = request.QInt("start-index", 0);
        var limit = Math.Min(request.QInt("max-results", request.QInt("max-result", 25)), PageLimit);
        var page = items.Skip(start).Take(limit).ToList();
        var body = wrap ?? new JsonObject { ["expand"] = collection, ["link"] = Link("rest/api/latest/" + collection) };
        body[collection] = new JsonObject { ["size"] = items.Count, ["expand"] = item, ["start-index"] = start, ["max-result"] = limit, [item] = new JsonArray(page.ToArray()) };
        return Response.Json(body);
    }

    private static bool CanSee(Account? user, Project project) => project.VisibleTo is null || user is not null && project.VisibleTo.Contains(user.Name);

    private static Response NotFound(string message = "Not Found") => Response.Json(new JsonObject { ["message"] = message, ["status-code"] = 404 }, 404);

    private JsonObject Link(string relative) => new() { ["href"] = $"{Server}/{relative}", ["rel"] = "self" };

    private static string? Iso(DateTimeOffset? time) => time?.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    private JsonObject ProjectJson(Project p) => new() { ["key"] = p.Key, ["name"] = p.Name, ["description"] = p.Description, ["link"] = Link($"rest/api/latest/project/{p.Key}") };

    private JsonObject PlanJson(Plan p) => new()
    {
        ["shortName"] = p.ShortName,
        ["shortKey"] = p.ShortKey,
        ["type"] = p.BranchOf is null ? "chain" : "chain_branch",
        ["enabled"] = p.Enabled,
        ["link"] = Link($"rest/api/latest/plan/{p.Key}"),
        ["key"] = p.Key,
        ["name"] = p.Name,
        ["projectKey"] = p.Project.Key,
        ["projectName"] = p.Project.Name,
        ["description"] = p.Description,
        ["isFavourite"] = false,
        ["isActive"] = p.Builds.Any(b => b.LifeCycle != "Finished"),
        ["isBuilding"] = p.Builds.Any(b => b.LifeCycle == "InProgress"),
        ["averageBuildTimeInSeconds"] = 42,
    };

    private JsonObject BranchJson(Plan b) => new()
    {
        ["description"] = b.Description,
        ["shortKey"] = b.ShortKey,
        ["shortName"] = b.ShortName,
        ["enabled"] = b.Enabled,
        ["link"] = Link($"rest/api/latest/plan/{b.Key}"),
        ["workflowType"] = "branch",
        ["key"] = b.Key,
        ["name"] = b.ShortName,
    };

    private JsonObject ResultJson(Build b, string expand, bool full)
    {
        var json = new JsonObject
        {
            ["expand"] = "changes,metadata,plan,master,artifacts,comments,labels,jiraIssues,stages,variables",
            ["link"] = Link($"rest/api/latest/result/{b.Key}"),
            ["plan"] = new JsonObject { ["shortName"] = b.Plan.ShortName, ["shortKey"] = b.Plan.ShortKey, ["type"] = b.Plan.BranchOf is null ? "chain" : "chain_branch", ["enabled"] = b.Plan.Enabled, ["link"] = Link($"rest/api/latest/plan/{b.Plan.Key}"), ["key"] = b.Plan.Key, ["name"] = b.Plan.Name },
            ["planName"] = b.Plan.ShortName,
            ["projectName"] = b.Plan.Project.Name,
            ["buildResultKey"] = b.Key,
            ["lifeCycleState"] = b.LifeCycle,
            ["id"] = 1000 + b.Number,
            ["buildStartedTime"] = Iso(b.Started),
            ["buildCompletedTime"] = Iso(b.Completed),
            ["buildDurationInSeconds"] = b.Completed is { } c ? (long)(c - b.Started).TotalSeconds : 0,
            ["buildDuration"] = b.Completed is { } c2 ? (long)(c2 - b.Started).TotalMilliseconds : 0,
            ["buildRelativeTime"] = "1 day ago",
            ["vcsRevisionKey"] = b.Changes.LastOrDefault()?.ChangesetId,
            ["buildTestSummary"] = b.Failed > 0 ? $"{b.Failed} of {b.Passed + b.Failed} failed" : $"{b.Passed} passed",
            ["successfulTestCount"] = b.Passed,
            ["failedTestCount"] = b.Failed,
            ["quarantinedTestCount"] = b.Quarantined,
            ["skippedTestCount"] = b.Skipped,
            ["continuable"] = false,
            ["onceOff"] = false,
            ["restartable"] = false,
            ["notRunYet"] = false,
            ["finished"] = b.LifeCycle == "Finished",
            ["successful"] = b.State == "Successful",
            ["buildReason"] = b.Reason,
            ["reasonSummary"] = b.Reason,
            ["key"] = b.Key,
            ["state"] = b.State,
            ["buildState"] = b.State,
            ["number"] = b.Number,
            ["buildNumber"] = b.Number,
        };
        if (!full)
        {
            return json;
        }

        if (expand.Contains("stages", StringComparison.Ordinal))
        {
            var stages = b.JobRuns.GroupBy(r => r.Job.Stage).Select(g => (JsonNode)new JsonObject
            {
                ["name"] = g.Key,
                ["results"] = new JsonObject { ["size"] = g.Count(), ["result"] = new JsonArray(g.Select(r => (JsonNode)JobResultJson(r, expand)).ToArray()) },
            }).ToArray();
            json["stages"] = new JsonObject { ["size"] = stages.Length, ["stage"] = new JsonArray(stages) };
        }

        if (expand.Contains("changes", StringComparison.Ordinal))
        {
            json["changes"] = new JsonObject
            {
                ["size"] = b.Changes.Count,
                ["change"] = new JsonArray(b.Changes.Select(c => (JsonNode)new JsonObject
                {
                    ["author"] = c.Author.ToLowerInvariant().Replace(' ', '.'),
                    ["fullName"] = c.Author,
                    ["userName"] = c.Author.ToLowerInvariant().Replace(' ', '.'),
                    ["changesetId"] = c.ChangesetId,
                    ["date"] = Iso(c.Date),
                    ["comment"] = c.Comment,
                    ["files"] = new JsonObject { ["size"] = c.Files.Count, ["file"] = new JsonArray(c.Files.Select(f => (JsonNode)new JsonObject { ["name"] = f, ["revision"] = c.ChangesetId }).ToArray()) },
                }).ToArray()),
            };
        }

        if (expand.Contains("jiraIssues", StringComparison.Ordinal))
        {
            json["jiraIssues"] = new JsonObject { ["size"] = b.Issues.Count, ["issue"] = new JsonArray(b.Issues.Select(i => (JsonNode)new JsonObject { ["key"] = i.Key, ["url"] = i.Url }).ToArray()) };
        }

        return json;
    }

    private JsonObject JobResultJson(JobRun r, string expand)
    {
        var json = new JsonObject
        {
            ["expand"] = "changes,metadata,plan,master,artifacts,comments,labels,jiraIssues,stages,variables",
            ["link"] = Link($"rest/api/latest/result/{r.Key}"),
            ["plan"] = new JsonObject { ["shortName"] = r.Job.Name, ["shortKey"] = r.Job.ShortKey, ["type"] = "job", ["enabled"] = true, ["link"] = Link($"rest/api/latest/plan/{r.JobKey}"), ["key"] = r.JobKey, ["name"] = $"{r.Build.Plan.Name} - {r.Job.Name}" },
            ["buildResultKey"] = r.Key,
            ["lifeCycleState"] = r.LifeCycle,
            ["id"] = 2000 + r.Build.Number,
            ["buildStartedTime"] = Iso(r.Started),
            ["buildCompletedTime"] = Iso(r.Completed),
            ["key"] = r.Key,
            ["state"] = r.State,
            ["buildState"] = r.State,
            ["number"] = r.Build.Number,
            ["buildNumber"] = r.Build.Number,
        };
        if (expand.Contains("artifacts", StringComparison.Ordinal))
        {
            json["artifacts"] = new JsonObject
            {
                ["size"] = r.Artifacts.Count,
                ["artifact"] = new JsonArray(r.Artifacts.Select(a => (JsonNode)new JsonObject
                {
                    ["name"] = a.Name,
                    ["link"] = new JsonObject { ["href"] = ArtifactHref(r, a), ["rel"] = "self" },
                    ["producerJobKey"] = r.JobKey,
                    ["shared"] = a.Shared,
                    ["size"] = a.Size,
                    ["prettySizeDescription"] = $"{a.Size} bytes",
                }).ToArray()),
            };
        }

        if (expand.Contains("logFiles", StringComparison.Ordinal))
        {
            json["logFiles"] = new JsonArray((JsonNode)$"{Server}/download/{r.JobKey}/build_logs/{r.Key}.log");
        }

        return json;
    }

    /// <summary>Where Bamboo links an artifact: the page of its folder, or its one file.</summary>
    private string ArtifactHref(JobRun r, Artifact a)
    {
        var folder = $"{Server}/browse/{r.Key}/artifact/{r.Job.ShortKey}/{Uri.EscapeDataString(a.Name)}/";
        return a.SingleFile && a.Files.Count == 1 ? folder + Uri.EscapeDataString(a.Files.Keys.First()) : folder;
    }
}
