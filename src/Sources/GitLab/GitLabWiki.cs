using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnyFS.Sdk;

namespace AnyFS.Sources.GitLab;

/// <summary>A wiki page: its slug (its path in the wiki, <c>/</c> between levels), title, markup and content.</summary>
internal sealed record GlWikiPage(string Slug, string Title, string Format, string Content)
{
    public static GlWikiPage Parse(JsonElement e) => new(e.Str("slug") ?? "", e.Str("title") ?? "", e.Str("format") ?? "markdown", e.Str("content") ?? "");

    /// <summary>The file's extension for its markup, as GitLab keeps it in the wiki's repository.</summary>
    public string Extension => Format.ToLowerInvariant() switch
    {
        "asciidoc" => ".asciidoc",
        "rdoc" => ".rdoc",
        "org" => ".org",
        _ => ".md",
    };
}

/// <summary>Whose wiki: a project's, or a group's (GitLab Premium).</summary>
/// <param name="Api">The API's path for it: <c>projects/12</c>, <c>groups/3</c>.</param>
/// <param name="Web">Its pages' path on the server: <c>group/project/-/wikis</c>, <c>groups/group/-/wikis</c>.</param>
internal sealed record WikiOwner(string Key, string Api, string Web, DateTimeOffset? Modified)
{
    public static WikiOwner Of(GlProject p) => new($"p{p.Id}", $"projects/{p.Id}", $"{p.FullPath}/-/wikis", p.LastActivity);

    public static WikiOwner Of(GlGroup g) => new($"g{g.Id}", $"groups/{g.Id}", $"groups/{g.FullPath}/-/wikis", g.Created);
}

/// <summary>A file a wiki page embeds, as the page links it: <c>uploads/&lt;hash&gt;/&lt;name&gt;</c>, in the wiki's repository.</summary>
internal sealed record GlWikiUpload(string Hash, string FileName)
{
    public string Name => Uri.UnescapeDataString(FileName);
}

/// <summary>
/// A wiki (spec 7.8): its pages as files of their own markup, by their slugs (a <c>/</c> makes a folder: <c>guides/install</c>
/// is <c>guides\install.md</c>, beside <c>guides\install\</c> for the pages under it); and, at its top, <c>attachments</c>,
/// the files its pages embed. GitLab serves those only to a browser session, not to a token (spike D): with an access
/// token the pages' links point at GitLab instead.
/// </summary>
internal sealed partial class WikiFolder(WikiOwner owner, string prefix, string name) : GlFolder(name, owner.Modified)
{
    public const string Attachments = "attachments";

    [GeneratedRegex(@"(?<![\w/.-])uploads/(?<hash>[0-9a-f]{32})/(?<name>[^)\s""'\]>]+)", RegexOptions.CultureInvariant)]
    private static partial Regex WikiUpload();

    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var pages = await source.WikiAsync(owner, ct).ConfigureAwait(false);
        var level = new SortedDictionary<string, (GlWikiPage? Page, bool Folder)>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            if (!page.Slug.StartsWith(prefix, StringComparison.Ordinal) || page.Slug.Length == prefix.Length)
            {
                continue;
            }

            var rest = page.Slug[prefix.Length..];
            var slash = rest.IndexOf('/');
            var segment = slash < 0 ? rest : rest[..slash];
            var entry = level.GetValueOrDefault(segment);
            level[segment] = slash < 0 ? (page, entry.Folder) : (entry.Page, true);
        }

        // a page and the folder of the pages under it share a segment: install.md beside install\
        var raw = new List<string>();
        var make = new List<Func<string, GlNode>>();
        var depth = prefix.Count(c => c == '/');
        var cookies = source.Session.Method != GitLabSignIn.AccessToken;
        foreach (var (segment, (page, folder)) in level)
        {
            if (page is not null)
            {
                raw.Add(Uri.UnescapeDataString(segment) + page.Extension);
                make.Add(n => new TextFile(n, Text(source, owner, page, depth, cookies), owner.Modified));
            }

            if (folder)
            {
                raw.Add(Uri.UnescapeDataString(segment));
                make.Add(n => new WikiFolder(owner, prefix + segment + "/", n));
            }
        }

        if (prefix.Length == 0 && cookies && Uploads(pages).Count > 0)
        {
            raw.Add(Attachments);
            make.Add(n => new WikiAttachmentsFolder(owner, Uploads(pages), n));
        }

        var names = GitLabNames.Unique(raw);
        return make.Select((m, i) => m(names[i])).ToList();
    }

    /// <summary>The files the pages embed, each once.</summary>
    public static IReadOnlyList<GlWikiUpload> Uploads(IEnumerable<GlWikiPage> pages)
        => pages.SelectMany(p => WikiUpload().Matches(p.Content).Select(m => new GlWikiUpload(m.Groups["hash"].Value, m.Groups["name"].Value)))
            .DistinctBy(u => (u.Hash, u.FileName)).ToList();

    /// <summary>
    /// A page as its file: the page's address on the first line, as a comment its markup keeps (Markdown: the address,
    /// then the title as a heading unless the page starts with one; AsciiDoc: <c>// address</c>; Org: <c># address</c>;
    /// RDoc as it is), then the content as GitLab keeps it, its embedded files' links pointing at <c>attachments</c> (or,
    /// without a browser session, at GitLab).
    /// </summary>
    internal static byte[] Text(GitLabSource source, WikiOwner owner, GlWikiPage page, int depth, bool cookies)
    {
        var address = source.Api.Address.Page($"{owner.Web}/{page.Slug}").AbsoluteUri;
        var content = WikiUpload().Replace(page.Content.ReplaceLineEndings("\n"), m => cookies
            ? $"{string.Concat(Enumerable.Repeat("../", depth))}{Attachments}/{m.Groups["hash"].Value}/{m.Groups["name"].Value}"
            : source.Api.Address.Page($"{owner.Web}/uploads/{m.Groups["hash"].Value}/{m.Groups["name"].Value}").AbsoluteUri);
        var text = new StringBuilder();
        switch (page.Extension)
        {
            case ".md":
                text.Append(address).Append("\n\n");
                if (!content.TrimStart().StartsWith("# ", StringComparison.Ordinal))
                {
                    text.Append("# ").Append(string.Join(' ', page.Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).Append("\n\n");
                }

                break;
            case ".asciidoc":
                text.Append("// ").Append(address).Append('\n');
                break;
            case ".org":
                text.Append("# ").Append(address).Append('\n');
                break;
        }

        text.Append(content.TrimEnd('\n')).Append('\n');
        return Encoding.UTF8.GetBytes(text.ToString());
    }
}

/// <summary>The files a wiki's pages embed, as the pages link them: <c>attachments\&lt;hash&gt;\&lt;name&gt;</c>.</summary>
internal sealed class WikiAttachmentsFolder(WikiOwner owner, IReadOnlyList<GlWikiUpload> uploads, string name) : GlFolder(name, owner.Modified)
{
    public override Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var hashes = uploads.Select(u => u.Hash).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return Task.FromResult<IReadOnlyList<GlNode>>(hashes.Select(h => (GlNode)new WikiUploadFolder(owner, uploads.Where(u => u.Hash == h).ToList(), h)).ToList());
    }
}

/// <summary>One upload's folder (GitLab keeps each upload under a hash of its own).</summary>
internal sealed class WikiUploadFolder(WikiOwner owner, IReadOnlyList<GlWikiUpload> uploads, string name) : GlFolder(name, owner.Modified)
{
    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var names = GitLabNames.Unique(uploads.Select(u => u.Name).ToList());
        var files = new List<GlNode>();
        for (var i = 0; i < uploads.Count; i++)
        {
            if (await source.WikiUploadSizeAsync(owner, uploads[i], ct).ConfigureAwait(false) is { } length)
            {
                files.Add(new WikiUploadFile(owner, uploads[i], names[i], length));
            }
        }

        return files;
    }
}

internal sealed class WikiUploadFile(WikiOwner owner, GlWikiUpload upload, string name, long length) : GlFile(name, length, owner.Modified, owner.Modified, upload.Hash)
{
    public override Task<IVfsFile> OpenAsync(GitLabSource source, CancellationToken ct) => source.OpenWikiUploadAsync(owner, upload, Length, ct);
}
