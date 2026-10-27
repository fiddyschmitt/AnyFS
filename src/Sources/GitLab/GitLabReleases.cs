using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnyFS.Sources.GitLab;

/// <summary>A release's link: a file uploaded to the project, a job's artifact, or anything on the web.</summary>
internal sealed record GlReleaseLink(string Name, string Url);

/// <summary>A release: its tag, name, notes and links.</summary>
internal sealed record GlRelease(string Tag, string Name, string Description, DateTimeOffset? Created, DateTimeOffset? Released, GlPerson? Author, IReadOnlyList<GlReleaseLink> Links)
{
    public static GlRelease Parse(JsonElement e) => new(
        e.Str("tag_name") ?? "",
        e.Str("name") ?? e.Str("tag_name") ?? "",
        e.Str("description") ?? "",
        e.Date("created_at"),
        e.Date("released_at"),
        GlPerson.Parse(e.Obj("author")),
        e.Obj("assets").Arr("links").Select(l => new GlReleaseLink(l.Str("name") ?? "", l.Str("direct_asset_url") ?? l.Str("url") ?? "")).ToList());
}

/// <summary>A project's releases, newest first: a folder each, named by its tag.</summary>
internal sealed class ReleasesFolder(GlProject project, string name) : GlFolder(name, project.LastActivity, project.Created)
{
    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var releases = await source.ReleasesAsync(project, ct).ConfigureAwait(false);
        var names = GitLabNames.Unique(releases.Select(r => r.Tag).ToList());
        return releases.Select((r, i) => (GlNode)new ReleaseFolder(project, r, names[i])).ToList();
    }
}

/// <summary>
/// A release: <c>release.md</c> (its name, tag, dates, author and notes) and <c>assets</c>: a file uploaded to the project
/// as itself, any other link as an Internet shortcut (<c>.url</c>).
/// </summary>
internal sealed partial class ReleaseFolder(GlProject project, GlRelease release, string name) : GlFolder(name, release.Released ?? release.Created, release.Created)
{
    public const string Assets = "assets";

    [GeneratedRegex(@"/uploads/(?<secret>[0-9a-f]{32})/(?<name>[^?#/]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex UploadLink();

    public override Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        // the notes' own uploads and the links to uploaded files, as files; any other link as a shortcut
        var uploads = GitLabDocuments.Uploads([release.Description]).ToList();
        var assets = new List<(GlUpload? Upload, GlReleaseLink? Link, string Raw)>();
        assets.AddRange(uploads.Select(u => ((GlUpload?)u, (GlReleaseLink?)null, u.Name)));
        foreach (var link in release.Links)
        {
            if (Uri.TryCreate(link.Url, UriKind.Absolute, out var url) && UploadLink().Match(url.AbsolutePath) is { Success: true } m)
            {
                var upload = new GlUpload(m.Groups["secret"].Value, m.Groups["name"].Value);
                if (!uploads.Contains(upload))
                {
                    assets.Add((upload, link, link.Name.Length > 0 ? link.Name : upload.Name));
                }
            }
            else
            {
                assets.Add((null, link, (link.Name.Length > 0 ? link.Name : link.Url) + ".url"));
            }
        }

        var names = GitLabNames.Unique(assets.Select(a => a.Raw).ToList());
        var byUpload = assets.Select((a, i) => (a.Upload, Name: names[i])).Where(a => a.Upload is not null).ToDictionary(a => a.Upload!, a => a.Name);
        var time = release.Released ?? release.Created;
        var children = new List<GlNode> { new TextFile("release.md", Document(byUpload), time, release.Created) };
        if (assets.Count > 0)
        {
            children.Add(new ReleaseAssetsFolder(project, assets.Select((a, i) => (a.Upload, a.Link, names[i])).ToList(), Assets, time));
        }

        return Task.FromResult<IReadOnlyList<GlNode>>(children);
    }

    private byte[] Document(IReadOnlyDictionary<GlUpload, string> uploads)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(GitLabDocuments.OneLine(release.Name.Length > 0 ? release.Name : release.Tag)).Append("\n\n");
        GitLabDocuments.Line(sb, "Tag", release.Tag);
        GitLabDocuments.Line(sb, "Released", GitLabDocuments.Date(release.Released));
        GitLabDocuments.Line(sb, "Created", GitLabDocuments.Date(release.Created));
        GitLabDocuments.Line(sb, "Author", release.Author?.ToString());
        foreach (var link in release.Links)
        {
            GitLabDocuments.Line(sb, "Link", $"[{link.Name}]({link.Url})");
        }

        sb.Append("\n---\n\n").Append(GitLabDocuments.LinkUploads(release.Description.ReplaceLineEndings("\n").Trim(), Assets, uploads)).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}

/// <summary>A release's files: those uploaded to the project, and a shortcut for each other link.</summary>
internal sealed class ReleaseAssetsFolder(GlProject project, IReadOnlyList<(GlUpload? Upload, GlReleaseLink? Link, string Name)> assets, string name, DateTimeOffset? modified) : GlFolder(name, modified, modified)
{
    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var files = new List<GlNode>();
        foreach (var (upload, link, shown) in assets)
        {
            if (upload is not null)
            {
                if ((await source.UploadSizeAsync(project, upload, ct).ConfigureAwait(false)).Value is { } length)
                {
                    files.Add(new UploadFile(project, upload, shown, length, Modified));
                }
            }
            else if (link is not null)
            {
                files.Add(new TextFile(shown, Encoding.UTF8.GetBytes($"[InternetShortcut]\r\nURL={link.Url}\r\n"), Modified));
            }
        }

        return files;
    }
}
