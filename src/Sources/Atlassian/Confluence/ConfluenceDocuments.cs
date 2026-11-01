using System.Text;

namespace AnyFS.Sources.Atlassian.Confluence;

/// <summary>
/// The Markdown a Confluence source makes of a page or blog post (spec 7.10): its address in Confluence on the first line
/// (as a SharePoint page's), its title, a block of <c>**Label:** value</c> lines (who made and last changed it, when, its
/// version, its labels), a rule, its body converted from the storage format, then its comments, oldest first.
/// </summary>
internal static class ConfluenceDocuments
{
    /// <summary>
    /// Bumped when what the files hold, or how the tree's folders are named, changes: the sizes in the listings a run
    /// before saved would no longer be the files', so they are not read back.
    /// </summary>
    public const int Format = 1;

    public const string BlogFolder = "Blog";
    public const string PersonalFolder = "Personal spaces";
    public const string ArchivedFolder = "Archived spaces";

    /// <summary>A page's folder: its title as a name.</summary>
    public static string PageFolderName(ConfluenceContent content)
        => content.IsBlogPost && content.PostingDay is { } day
            ? AtlassianNames.Safe($"{day:yyyy-MM-dd} {AtlassianNames.Title(content.Title)}")
            : AtlassianNames.Safe(AtlassianNames.Title(content.Title));

    /// <summary>A space's folder: <c>Engineering (ENG)</c>.</summary>
    public static string SpaceFolderName(ConfluenceSpace space) => $"{AtlassianNames.Title(space.Name)} ({space.Key})";

    /// <summary>The page's address in Confluence (its own web link, at the source's address).</summary>
    public static Uri WebAddress(ConfluenceContent content, Uri installation)
        => content.WebUi is { Length: > 0 } web ? new Uri(installation, web.TrimStart('/')) : new Uri(installation, $"pages/viewpage.action?pageId={content.Id}");

    /// <summary>The page's Markdown, UTF-8 without a byte order mark.</summary>
    /// <param name="comments">The comments to end with, oldest first; null leaves them out (the Comments setting off).</param>
    public static byte[] Page(ConfluenceContent content, StorageContext context, IReadOnlyList<ConfluenceComment>? comments)
    {
        var sb = new StringBuilder();
        sb.Append(WebAddress(content, context.Installation).AbsoluteUri).Append("\n\n");
        sb.Append("# ").Append(AtlassianNames.OneLine(content.Title)).Append("\n\n");
        AtlassianMarkdown.Line(sb, "Created", Who(content.CreatedBy, content.Created));
        AtlassianMarkdown.Line(sb, "Last changed", Who(content.ChangedBy, content.Changed) is { } changed ? $"{changed} (version {content.Version})" : null);
        AtlassianMarkdown.Line(sb, "Labels", content.Labels.Count == 0 ? null : string.Join(", ", content.Labels));
        sb.Append("\n---\n\n");

        var body = AtlassianMarkdown.FromHtml(ConfluenceStorage.ToHtml(content.Storage, context), null);
        if (body.Length > 0)
        {
            sb.Append(body).Append("\n\n");
        }

        if (comments is not null && comments.Count > 0)
        {
            // one listed twice (a page of them moved under an offset read) is one
            var byId = new Dictionary<string, ConfluenceComment>(StringComparer.Ordinal);
            foreach (var comment in comments)
            {
                byId.TryAdd(comment.Id, comment);
            }

            sb.Append("## Comments\n");
            foreach (var comment in comments)
            {
                sb.Append("\n### ").Append(comment.Author?.ToString() ?? "Anonymous").Append(", ").Append(AtlassianMarkdown.Date(comment.Created) ?? "");
                if (comment.ParentId is { } parent && byId.TryGetValue(parent, out var answered))
                {
                    sb.Append(" (reply to ").Append(answered.Author?.DisplayName ?? "a comment").Append(')');
                }

                if (comment.Inline)
                {
                    sb.Append(comment.Selection is { Length: > 0 } selection ? $" (on \"{AtlassianNames.OneLine(selection)}\")" : " (inline)");
                }

                sb.Append("\n\n");
                var text = AtlassianMarkdown.FromHtml(ConfluenceStorage.ToHtml(comment.Storage, context), null);
                if (text.Length > 0)
                {
                    sb.Append(text).Append('\n');
                }
            }
        }

        return AtlassianMarkdown.Bytes(sb);
    }

    private static string? Who(AtlassianUser? who, DateTimeOffset? when)
        => who is null && when is null ? null : $"{who?.ToString() ?? "someone"}, {AtlassianMarkdown.Date(when)}".TrimEnd(',', ' ');
}
