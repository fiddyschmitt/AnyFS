using System.Text;

namespace AnyFS.Sources.Atlassian.Bitbucket;

/// <summary>
/// The files a Bitbucket source makes (spec 7.11): a pull request's <c>pull-request.md</c> — its number and title, a block
/// of <c>**Label:** value</c> lines, a rule, the description as written (Bitbucket's descriptions are Markdown already),
/// then what happened to it, oldest first: the comments with their replies, the approvals, the merge or the decline — and
/// its <c>changes.diff</c>, the diff as Bitbucket gives it.
/// </summary>
internal static class BitbucketDocuments
{
    /// <summary>Bumped when what the files hold, or how the tree's folders are named, changes (the listings a run before saved are not read back).</summary>
    public const int Format = 1;

    public const string PullRequestFile = "pull-request.md";
    public const string DiffFile = "changes.diff";

    /// <summary>A pull request's folder name: <c>#12 Fix the build</c>.</summary>
    public static string FolderName(long id, string title) => AtlassianNames.Safe($"#{id} {AtlassianNames.Title(title)}".Trim());

    /// <summary>The pull request's <c>pull-request.md</c>.</summary>
    /// <param name="activities">What happened to it, oldest first; null leaves the comments out (the Comments setting off).</param>
    public static byte[] PullRequest(BbRepository repository, BbPullRequest pr, IReadOnlyList<BbActivity>? activities, Uri installation)
    {
        var sb = new StringBuilder();
        sb.Append("# #").Append(pr.Id).Append(' ').Append(AtlassianNames.OneLine(pr.Title)).Append("\n\n");
        AtlassianMarkdown.Line(sb, "State", pr.State is { Length: > 0 } state ? char.ToUpperInvariant(state[0]) + state[1..].ToLowerInvariant() : null);
        AtlassianMarkdown.Line(sb, "Author", pr.Author?.ToString());
        AtlassianMarkdown.Line(sb, "From", Ref(pr.FromBranch, pr.FromCommit));
        AtlassianMarkdown.Line(sb, "To", Ref(pr.ToBranch, pr.ToCommit));
        AtlassianMarkdown.Line(sb, "Reviewers", pr.Reviewers.Count == 0 ? null : string.Join(", ", pr.Reviewers.Select(r => $"{r.User} ({Status(r.Status)})")));
        AtlassianMarkdown.Line(sb, "Created", AtlassianMarkdown.Date(pr.Created));
        AtlassianMarkdown.Line(sb, "Updated", AtlassianMarkdown.Date(pr.Updated));
        AtlassianMarkdown.Line(sb, pr.State.Equals("MERGED", StringComparison.OrdinalIgnoreCase) ? "Merged" : "Closed", AtlassianMarkdown.Date(pr.Closed));
        AtlassianMarkdown.Line(sb, "Web", new Uri(installation, $"{repository.Web}/pull-requests/{pr.Id}").AbsoluteUri);
        sb.Append("\n---\n\n");
        if (!string.IsNullOrWhiteSpace(pr.Description))
        {
            sb.Append(AtlassianMarkdown.Tidy(pr.Description)).Append("\n\n");
        }

        if (activities is { Count: > 0 })
        {
            var told = activities.Where(a => a.Comment is not null || a.Action is "APPROVED" or "UNAPPROVED" or "REVIEWED" or "MERGED" or "DECLINED" or "REOPENED").OrderBy(a => a.Created ?? DateTimeOffset.MinValue).ThenBy(a => a.Id).ToList();
            if (told.Count > 0)
            {
                sb.Append("## Activity\n");
                foreach (var activity in told)
                {
                    if (activity.Comment is { } comment)
                    {
                        Comment(sb, comment, 3);
                    }
                    else
                    {
                        sb.Append("\n### ").Append(activity.User?.ToString() ?? "Someone").Append(", ").Append(AtlassianMarkdown.Date(activity.Created) ?? "").Append(": ").Append(Action(activity.Action)).Append('\n');
                    }
                }
            }
        }

        return AtlassianMarkdown.Bytes(sb);
    }

    /// <summary>A comment and its replies, each under its author and time, a reply one heading deeper.</summary>
    private static void Comment(StringBuilder sb, BbComment comment, int depth)
    {
        sb.Append('\n').Append('#', Math.Min(depth, 6)).Append(' ').Append(comment.Author?.ToString() ?? "Anonymous").Append(", ").Append(AtlassianMarkdown.Date(comment.Created) ?? "");
        if (comment.Anchor is { } anchor)
        {
            sb.Append(" (on ").Append(anchor).Append(')');
        }

        if (comment.Updated is { } edited && comment.Created is { } made && edited - made > TimeSpan.FromMinutes(1))
        {
            sb.Append(" (edited ").Append(AtlassianMarkdown.Date(edited)).Append(')');
        }

        sb.Append("\n\n");
        var text = AtlassianMarkdown.Tidy(comment.Text);
        if (text.Length > 0)
        {
            sb.Append(text).Append('\n');
        }

        foreach (var reply in comment.Replies.OrderBy(r => r.Created ?? DateTimeOffset.MinValue).ThenBy(r => r.Id))
        {
            Comment(sb, reply, depth + 1);
        }
    }

    private static string? Ref(string branch, string commit) => branch.Length == 0 ? null : commit.Length >= 11 ? $"{branch} ({commit[..11]})" : branch;

    private static string Status(string status) => status.ToUpperInvariant() switch
    {
        "APPROVED" => "approved",
        "NEEDS_WORK" => "needs work",
        _ => "not yet reviewed",
    };

    private static string Action(string action) => action.ToUpperInvariant() switch
    {
        "APPROVED" => "approved",
        "UNAPPROVED" => "withdrew the approval",
        "REVIEWED" => "reviewed: needs work",
        "MERGED" => "merged",
        "DECLINED" => "declined",
        "REOPENED" => "reopened",
        _ => action.ToLowerInvariant(),
    };
}
