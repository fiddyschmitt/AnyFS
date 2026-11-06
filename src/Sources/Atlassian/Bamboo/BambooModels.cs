using System.Text.Json;

namespace AnyFS.Sources.Atlassian.Bamboo;

/// <summary>What a Bamboo source's address names (spec 7.12).</summary>
internal enum BambooScopeKind
{
    /// <summary>The whole server: every project the account can see, and its plans.</summary>
    Server,

    /// <summary>One project's plans.</summary>
    Project,

    /// <summary>One plan (a plan branch is a plan with a key of its own).</summary>
    Plan,
}

/// <summary>A Bamboo source's scope: what kind, the project's key and the plan's.</summary>
internal sealed record BambooScope(BambooScopeKind Kind, string ProjectKey, string PlanKey)
{
    public static readonly BambooScope Server = new(BambooScopeKind.Server, "", "");

    /// <summary>What the scope names, for messages: <c>PROJ</c>, <c>PROJ-PLAN</c>, or empty for the server.</summary>
    public string Value => Kind switch
    {
        BambooScopeKind.Plan => PlanKey,
        BambooScopeKind.Project => ProjectKey,
        _ => "",
    };

    /// <summary>
    /// The scope the rest of an address names (below the installation): the dashboard or nothing for the server; a key
    /// after <c>browse/</c> (a project's <c>PROJ</c>, a plan's <c>PROJ-PLAN</c>, a build's <c>PROJ-PLAN-12</c>, a job's
    /// <c>PROJ-PLAN-JOB1</c> or a job result's <c>PROJ-PLAN-JOB1-12</c>, the last three cut back to the plan); a
    /// <c>planKey</c>, <c>buildKey</c> or <c>projectKey</c> in the query (the older pages). Null, with why, for an address
    /// that names nothing a source can show.
    /// </summary>
    public static BambooScope? From(AtlassianAddress address, out string? problem)
    {
        problem = null;
        var rest = address.Rest;
        if (rest.Count == 0)
        {
            return Server;
        }

        var first = rest[0];
        if (rest.Count == 1 && (first.Equals("allPlans.action", StringComparison.OrdinalIgnoreCase) || first.Equals("start.action", StringComparison.OrdinalIgnoreCase)
            || first.Equals("dashboard", StringComparison.OrdinalIgnoreCase) || first.Equals("browse", StringComparison.OrdinalIgnoreCase)))
        {
            return Server;
        }

        if (first.Equals("browse", StringComparison.OrdinalIgnoreCase) && rest.Count >= 2 && rest[1].Length > 0 && !rest[1].Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            return OfKey(rest[1]);
        }

        foreach (var name in new[] { "planKey", "buildKey", "projectKey" })
        {
            if (address.Query.TryGetValue(name, out var key) && key.Length > 0)
            {
                return OfKey(key);
            }
        }

        problem = "The address names nothing a Bamboo source shows: give the server's address, a project's (…/browse/PROJ) or a plan's (…/browse/PROJ-PLAN).";
        return null;
    }

    /// <summary>A scope from a key: <c>PROJ</c> a project; <c>PROJ-PLAN</c> a plan; a build's, a job's or a job result's key the plan they belong to.</summary>
    public static BambooScope OfKey(string key)
    {
        var parts = key.Trim().ToUpperInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => Server,
            1 => new BambooScope(BambooScopeKind.Project, parts[0], ""),
            _ => new BambooScope(BambooScopeKind.Plan, parts[0], parts[0] + "-" + parts[1]),
        };
    }

    /// <summary>The REST path of a project, a plan or a result: <c>project/PROJ</c>, <c>plan/PROJ-PLAN</c>, <c>result/PROJ-PLAN-12</c>.</summary>
    public static string Path(string kind, string key) => kind + "/" + Uri.EscapeDataString(key);
}

/// <summary>A project, as the project list and the project call give it.</summary>
internal sealed record BmProject(string Key, string Name, string? Description)
{
    public static BmProject Parse(JsonElement e) => new(e.Str("key") ?? "", e.Str("name") ?? e.Str("key") ?? "", e.Str("description"));
}

/// <summary>A plan as its list and its call give it, or a plan branch (a plan of its own, <c>PROJ-PLAN12</c>, its short name the branch's).</summary>
/// <param name="Name">The full name, <c>Project - Plan</c>.</param>
/// <param name="ShortName">The plan's own name, or the branch's.</param>
internal sealed record BmPlan(string Key, string ShortKey, string Name, string ShortName, string ProjectKey, string? ProjectName, bool Enabled, string? Description, string Type)
{
    public static BmPlan Parse(JsonElement e)
    {
        var key = e.Str("key") ?? "";
        var dash = key.IndexOf('-');
        return new BmPlan(
            key,
            e.Str("shortKey") ?? (dash > 0 ? key[(dash + 1)..] : key),
            e.Str("name") ?? key,
            e.Str("shortName") ?? e.Str("name") ?? key,
            e.Str("projectKey") ?? (dash > 0 ? key[..dash] : ""),
            e.Str("projectName"),
            !e.Has("enabled") || e.Bool("enabled"),
            e.Str("description"),
            e.Str("type") ?? "chain");
    }

    public bool IsBranch => Type.Equals("chain_branch", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A build (Bamboo's result) as its list gives it: its number and key, how it went, when, and its tests.</summary>
internal sealed record BmResult(string Key, long Number, string State, string LifeCycle, DateTimeOffset? Started, DateTimeOffset? Completed, long DurationSeconds, string? Reason, long Passed, long Failed, long Skipped, long Quarantined)
{
    public static BmResult Parse(JsonElement e) => new(
        e.Str("buildResultKey") ?? e.Str("key") ?? "",
        e.Has("buildNumber") ? e.Long("buildNumber") : e.Long("number"),
        e.Str("buildState") ?? e.Str("state") ?? "Unknown",
        e.Str("lifeCycleState") ?? "Finished",
        e.Date("buildStartedTime"),
        e.Date("buildCompletedTime"),
        e.Long("buildDurationInSeconds"),
        e.Str("buildReason"),
        e.Long("successfulTestCount"),
        e.Long("failedTestCount"),
        e.Long("skippedTestCount"),
        e.Long("quarantinedTestCount"));

    /// <summary>Finished, or never built: nothing about it changes any more.</summary>
    public bool Final => LifeCycle is "Finished" or "NotBuilt";

    /// <summary>How it went, as its folder's name says: <c>Successful</c>, <c>Failed</c>; <c>In progress</c>, <c>Queued</c> while it is not final.</summary>
    public string Outcome => LifeCycle switch
    {
        "Finished" => State.Length > 0 ? State : "Unknown",
        "NotBuilt" => "Not built",
        "InProgress" => "In progress",
        _ => LifeCycle.Length > 0 ? LifeCycle : "Unknown",
    };
}

/// <summary>One job's result in a build: which job, how it went, its artifacts and its log files (addresses Bamboo serves).</summary>
internal sealed record BmJobResult(string Key, string JobKey, string JobShortKey, string JobName, string State, string LifeCycle, DateTimeOffset? Started, DateTimeOffset? Completed, IReadOnlyList<BmArtifact> Artifacts, IReadOnlyList<string> LogFiles)
{
    public static BmJobResult Parse(JsonElement e)
    {
        var plan = e.Obj("plan");
        var key = e.Str("buildResultKey") ?? e.Str("key") ?? "";
        var jobKey = plan.Str("key") ?? (key.LastIndexOf('-') is > 0 and var dash ? key[..dash] : key);
        var jobDash = jobKey.LastIndexOf('-');
        return new BmJobResult(
            key,
            jobKey,
            plan.Str("shortKey") ?? (jobDash > 0 ? jobKey[(jobDash + 1)..] : jobKey),
            plan.Str("shortName") ?? plan.Str("name") ?? jobKey,
            e.Str("buildState") ?? e.Str("state") ?? "Unknown",
            e.Str("lifeCycleState") ?? "Finished",
            e.Date("buildStartedTime"),
            e.Date("buildCompletedTime"),
            e.Obj("artifacts").Arr("artifact").Select(BmArtifact.Parse).Where(a => a.Name.Length > 0 && a.Href.Length > 0).ToList(),
            e.Arr("logFiles").Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!).Where(l => l.Length > 0).ToList());
    }
}

/// <summary>An artifact a job kept: its name, the address of its page (a folder of files, or one file), and its size as Bamboo counts it.</summary>
internal sealed record BmArtifact(string Name, string Href, string? ProducerJobKey, bool Shared, long Size)
{
    public static BmArtifact Parse(JsonElement e) => new(e.Str("name") ?? "", e.Obj("link").Str("href") ?? "", e.Str("producerJobKey"), e.Bool("shared"), e.Long("size"));
}

/// <summary>A change a build built: who made it, when, what it says, the files it touched.</summary>
internal sealed record BmChange(string? Author, string? ChangesetId, DateTimeOffset? Date, string? Comment, IReadOnlyList<string> Files)
{
    public static BmChange Parse(JsonElement e) => new(
        e.Str("fullName") ?? e.Str("author") ?? e.Str("userName"),
        e.Str("changesetId"),
        e.Date("date"),
        e.Str("comment"),
        e.Obj("files").Arr("file").Select(f => f.Str("name") ?? "").Where(n => n.Length > 0).ToList());
}

/// <summary>A Jira issue a build's changes name.</summary>
internal sealed record BmIssue(string Key, string? Url)
{
    public static BmIssue Parse(JsonElement e) => new(e.Str("key") ?? "", e.Str("url"));
}

/// <summary>A build whole, as the result call gives it: the result, the jobs that ran in it stage by stage, the changes it built and the Jira issues they name.</summary>
internal sealed record BmBuild(BmResult Result, IReadOnlyList<BmJobResult> Jobs, IReadOnlyList<BmChange> Changes, IReadOnlyList<BmIssue> Issues)
{
    public static BmBuild Parse(JsonElement e)
    {
        var jobs = new List<BmJobResult>();
        foreach (var stage in e.Obj("stages").Arr("stage"))
        {
            jobs.AddRange(stage.Obj("results").Arr("result").Select(BmJobResult.Parse).Where(j => j.Key.Length > 0));
        }

        return new BmBuild(
            BmResult.Parse(e),
            jobs,
            e.Obj("changes").Arr("change").Select(BmChange.Parse).ToList(),
            e.Obj("jiraIssues").Arr("issue").Select(BmIssue.Parse).Where(i => i.Key.Length > 0).ToList());
    }
}

/// <summary>An entry of an artifact's folder, from the page Bamboo shows for it: a file, or a folder below it.</summary>
internal sealed record BmArtifactEntry(string Name, bool IsDirectory, Uri Url);
