using System.Text;

namespace AnyFS.Sources.Atlassian.Bamboo;

/// <summary>
/// The files a Bamboo source makes (spec 7.12): a plan's <c>plan.md</c> (its name and key, project, whether it is enabled,
/// its web address, its description) and a build's <c>build.md</c> — how it went, why it ran, when and how long, its tests,
/// a table of its jobs, the changes it built with their files, the Jira issues they name. The usual shape: a title, a block
/// of <c>**Label:** value</c> lines, a rule, then the sections.
/// </summary>
internal static class BambooDocuments
{
    /// <summary>Bumped when what the files hold, or how the tree's folders are named, changes (the listings a run before saved are not read back).</summary>
    public const int Format = 1;

    public const string PlanFile = "plan.md";
    public const string BuildFile = "build.md";
    public const string BranchesFolder = "branches";
    public const string ArtifactsFolder = "artifacts";

    /// <summary>A build's folder name: <c>#12 Successful</c>, <c>#13 Failed</c>, <c>#14 In progress</c>.</summary>
    public static string BuildFolderName(BmResult result) => AtlassianNames.Safe($"#{result.Number} {result.Outcome}");

    /// <summary>A project's folder name: <c>Alpha (PROJ)</c>.</summary>
    public static string ProjectFolderName(BmProject project) => $"{AtlassianNames.Title(project.Name)} ({project.Key})";

    /// <summary>A plan's or a branch's folder name: <c>Build the app (PROJ-PLAN)</c>, <c>feature-x (PROJ-PLAN12)</c>.</summary>
    public static string PlanFolderName(BmPlan plan) => $"{AtlassianNames.Title(plan.ShortName)} ({plan.Key})";

    /// <summary>The plan's <c>plan.md</c>.</summary>
    public static byte[] Plan(BmPlan plan, Uri installation)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(AtlassianNames.OneLine(plan.ShortName)).Append(" (").Append(plan.Key).Append(")\n\n");
        AtlassianMarkdown.Line(sb, "Project", plan.ProjectName is { Length: > 0 } project ? $"{project} ({plan.ProjectKey})" : plan.ProjectKey);
        AtlassianMarkdown.Line(sb, "Kind", plan.IsBranch ? "Plan branch" : "Plan");
        AtlassianMarkdown.Line(sb, "Enabled", plan.Enabled ? "yes" : "no");
        AtlassianMarkdown.Line(sb, "Web", new Uri(installation, "browse/" + Uri.EscapeDataString(plan.Key)).AbsoluteUri);
        sb.Append("\n---\n\n");
        if (!string.IsNullOrWhiteSpace(plan.Description))
        {
            sb.Append(AtlassianMarkdown.Tidy(plan.Description)).Append('\n');
        }

        return AtlassianMarkdown.Bytes(sb);
    }

    /// <summary>The build's <c>build.md</c>.</summary>
    public static byte[] Build(BmPlan plan, BmBuild build, Uri installation)
    {
        var r = build.Result;
        var sb = new StringBuilder();
        sb.Append("# #").Append(r.Number).Append(' ').Append(AtlassianNames.OneLine(plan.ShortName)).Append("\n\n");
        AtlassianMarkdown.Line(sb, "Key", r.Key);
        AtlassianMarkdown.Line(sb, "State", r.Outcome);
        AtlassianMarkdown.Line(sb, "Reason", AtlassianMarkdown.FromHtml(r.Reason, installation));
        AtlassianMarkdown.Line(sb, "Started", AtlassianMarkdown.Date(r.Started));
        AtlassianMarkdown.Line(sb, "Completed", AtlassianMarkdown.Date(r.Completed));
        AtlassianMarkdown.Line(sb, "Took", r.Final && r.DurationSeconds > 0 ? Took(r.DurationSeconds) : null);
        AtlassianMarkdown.Line(sb, "Tests", Tests(r));
        AtlassianMarkdown.Line(sb, "Web", new Uri(installation, "browse/" + Uri.EscapeDataString(r.Key)).AbsoluteUri);
        sb.Append("\n---\n");

        if (build.Jobs.Count > 0)
        {
            sb.Append("\n## Jobs\n\n| Job | State | Started | Took | Artifacts |\n|---|---|---|---|---|\n");
            foreach (var job in build.Jobs)
            {
                var took = job.Started is { } s && job.Completed is { } c && c >= s ? Took((long)(c - s).TotalSeconds) : "";
                sb.Append("| ").Append(Cell(job.JobName)).Append(" | ").Append(Cell(job.LifeCycle == "Finished" ? job.State : job.LifeCycle)).Append(" | ").Append(AtlassianMarkdown.Date(job.Started) ?? "")
                    .Append(" | ").Append(took).Append(" | ").Append(Cell(string.Join(", ", job.Artifacts.Select(a => a.Name)))).Append(" |\n");
            }
        }

        if (build.Changes.Count > 0)
        {
            sb.Append("\n## Changes\n");
            foreach (var change in build.Changes)
            {
                sb.Append("\n### ").Append(AtlassianNames.OneLine(change.Author ?? "Someone")).Append(", ").Append(AtlassianMarkdown.Date(change.Date) ?? "");
                if (change.ChangesetId is { Length: > 0 } id)
                {
                    sb.Append(" (").Append(id.Length > 11 ? id[..11] : id).Append(')');
                }

                sb.Append("\n\n");
                var comment = AtlassianMarkdown.Tidy(change.Comment ?? "");
                if (comment.Length > 0)
                {
                    sb.Append(comment).Append('\n');
                }

                if (change.Files.Count > 0)
                {
                    sb.Append('\n');
                    foreach (var file in change.Files)
                    {
                        sb.Append("- ").Append(AtlassianNames.OneLine(file)).Append('\n');
                    }
                }
            }
        }

        if (build.Issues.Count > 0)
        {
            sb.Append("\n## Jira issues\n\n");
            foreach (var issue in build.Issues)
            {
                sb.Append("- ").Append(issue.Url is { Length: > 0 } url ? $"[{issue.Key}]({url})" : issue.Key).Append('\n');
            }
        }

        return AtlassianMarkdown.Bytes(sb);
    }

    /// <summary>A duration as people say it: <c>42 s</c>, <c>3 min 5 s</c>, <c>2 h 10 min</c>.</summary>
    public static string Took(long seconds) => seconds switch
    {
        < 60 => $"{seconds} s",
        < 3600 => $"{seconds / 60} min {seconds % 60} s",
        _ => $"{seconds / 3600} h {seconds % 3600 / 60} min",
    };

    private static string? Tests(BmResult r)
    {
        if (r.Passed + r.Failed + r.Skipped + r.Quarantined == 0)
        {
            return null;
        }

        var parts = new List<string> { $"{r.Passed:N0} passed" };
        if (r.Failed > 0)
        {
            parts.Add($"{r.Failed:N0} failed");
        }

        if (r.Skipped > 0)
        {
            parts.Add($"{r.Skipped:N0} skipped");
        }

        if (r.Quarantined > 0)
        {
            parts.Add($"{r.Quarantined:N0} quarantined");
        }

        return string.Join(", ", parts);
    }

    private static string Cell(string text) => AtlassianNames.OneLine(text).Replace("|", "\\|", StringComparison.Ordinal);
}
