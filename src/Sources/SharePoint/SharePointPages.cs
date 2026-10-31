using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using ReverseMarkdown;

namespace AnyFS.Sources.SharePoint;

/// <summary>
/// A site's pages (the Site Pages library's .aspx files) as Markdown files: Page.aspx shows as Page.aspx.md, read-only,
/// its content the page's address on the first line, its title as a heading and then its body, converted from the HTML
/// SharePoint keeps it in: a modern page's CanvasContent1, a wiki page's WikiField. A modern page holds each text web
/// part as HTML and, for the other web parts, the server's searchable rendering of them (captions, the addresses of
/// images and links) beside their data; that rendering stays, the web parts' type ids go. Addresses become absolute,
/// so links and images work away from SharePoint. The conversion is ReverseMarkdown's (MarkItDown converts HTML with
/// its Python counterpart).
/// </summary>
internal static partial class SharePointPages
{
    /// <summary>
    /// Bumped when what a page's Markdown holds changes: the sizes in the listings a run before saved would no longer be
    /// the files', so they are not read back.
    /// </summary>
    public const int Format = 1;

    /// <summary>The list fields a page's content comes from.</summary>
    public const string Fields = "Title,CanvasContent1,WikiField";

    /// <summary>The same for a SharePoint without modern pages (no CanvasContent1 field).</summary>
    public const string WikiFields = "Title,WikiField";

    /// <summary>What says a library is the site's pages: SharePoint's list template for Site Pages (REST, Graph).</summary>
    public const int PageLibraryTemplate = 119;

    public const string GraphTemplate = "webPageLibrary";

    private const string PageExtension = ".aspx";
    private const string ShownExtension = ".md";

    // the default flavour: its GitHub and CommonMark flavours keep a page's div blocks as raw HTML (ReverseMarkdown
    // 6.2.1); the default writes tables, fenced code and nested lists the GitHub way all the same
    private static readonly Lock ConverterGate = new();
    private static readonly Converter MarkdownConverter = new(new Config
    {
        Tags = { Unknown = Config.UnknownTagsOption.Bypass },
        Links = { SmartHref = true },
        Formatting = { RemoveComments = true, ListBulletChar = '-', OutputLineEnding = "\n" },
    });

    /// <summary>A page upstream: an .aspx file (anything else in a page library is left out).</summary>
    public static bool IsPage(string upstreamName) => upstreamName.Length > PageExtension.Length && upstreamName.EndsWith(PageExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The name a page shows as: its own with .md after it, Page.aspx as Page.aspx.md.</summary>
    public static string Shown(string upstreamName) => upstreamName + ShownExtension;

    /// <summary>The page a path in a page library means (a/Page.aspx.md is a/Page.aspx). Null for anything else: a folder, or a file that cannot be a page.</summary>
    public static string? Upstream(string itemPath)
    {
        var name = itemPath[(itemPath.LastIndexOf('/') + 1)..];
        return name.EndsWith(ShownExtension, StringComparison.OrdinalIgnoreCase) && IsPage(name[..^ShownExtension.Length]) ? itemPath[..^ShownExtension.Length] : null;
    }

    /// <summary>The page as Markdown, UTF-8 without a byte order mark: its full address on the first line (where known), "# Title", then the body.</summary>
    public static byte[] Markdown(string? title, string? canvasHtml, string? wikiHtml, Uri? pageUrl)
    {
        var text = new StringBuilder();
        if (pageUrl is not null)
        {
            text.Append(pageUrl.AbsoluteUri).Append("\n\n");
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            text.Append("# ").Append(WhiteSpace().Replace(title.Trim(), " ")).Append("\n\n");
        }

        // a modern page keeps its body in the canvas; a wiki page (and a modern page's leftover wiki field) in WikiField
        var html = !string.IsNullOrWhiteSpace(canvasHtml) ? canvasHtml : wikiHtml;
        if (!string.IsNullOrWhiteSpace(html))
        {
            string body;
            lock (ConverterGate)
            {
                body = MarkdownConverter.Convert(Clean(html, pageUrl));
            }

            body = BlankLines().Replace(body.Replace("\r\n", "\n"), "\n\n").Trim();
            if (body.Length > 0)
            {
                text.Append(body).Append('\n');
            }
        }

        return Encoding.UTF8.GetBytes(text.ToString().TrimEnd('\n') + (text.Length > 0 ? "\n" : ""));
    }

    /// <summary>
    /// A page that is in no pages library (a classic site's default.aspx, its web parts rendered by the server) as Markdown:
    /// its address and title, then the page's main content area as SharePoint renders it, without the ribbon, the
    /// navigation, the forms' fields and what is hidden. Through the same clean-up and conversion as a page in Site Pages.
    /// </summary>
    public static byte[] MarkdownFromPage(string html, Uri pageUrl)
    {
        var document = new HtmlParser().ParseDocument(html);
        var title = document.QuerySelector("#DeltaPlaceHolderPageTitleInTitleArea")?.TextContent is { } heading && !string.IsNullOrWhiteSpace(heading) ? heading : document.Title;
        var main = document.QuerySelector("#DeltaPlaceHolderMain") ?? document.QuerySelector("#contentBox") ?? document.QuerySelector("[role=main]") ?? document.Body;
        if (main is null)
        {
            return Markdown(title, null, null, pageUrl);
        }

        foreach (var element in main.QuerySelectorAll("input, select, button, textarea, .ms-hide, .ms-hidden, [style*='display:none'], [style*='display: none'], .ms-webpart-menuLink, .ms-wpTdSpace, .ms-qcb-root, .ms-list-addnew, .ms-csrlistview-controldiv").ToList())
        {
            element.Remove();
        }

        return Markdown(title, main.InnerHtml, null, pageUrl);
    }

    /// <summary>A text field of a list item's fields (REST's ListItemAllFields, Graph's listItem.fields); null when absent or empty.</summary>
    public static string? Field(JsonElement fields, string name)
        => fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The page's Markdown from its list item's fields.</summary>
    public static byte[] Markdown(JsonElement fields, Uri? pageUrl) => Markdown(Field(fields, "Title"), Field(fields, "CanvasContent1"), Field(fields, "WikiField"), pageUrl);

    /// <summary>The version a page's Markdown is cached under: its own hash, so an edited page, or one converted differently, is a new file.</summary>
    public static string Version(byte[] markdown) => "md:" + Convert.ToHexString(SHA256.HashData(markdown))[..20];

    /// <summary>What reading a page from <paramref name="offset"/> answers, as a download would: 200 with the rest, or 416 past the end.</summary>
    public static HttpResponseMessage Response(byte[] markdown, long offset)
    {
        if (offset > 0 && offset >= markdown.Length)
        {
            return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
        }

        var start = (int)Math.Min(offset, markdown.Length);
        return new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(markdown, start, markdown.Length - start) };
    }

    /// <summary>
    /// The page's HTML ready for conversion: what is not content gone (web parts' type ids, scripts, a wiki page's
    /// hidden web part markers), a wiki page's layout table taken apart into its zones, addresses made absolute.
    /// </summary>
    internal static string Clean(string html, Uri? pageUrl)
    {
        var document = new HtmlParser().ParseDocument("<!DOCTYPE html><html><body>" + html + "</body></html>");
        var body = document.Body!;
        foreach (var element in body.QuerySelectorAll("[data-sp-componentid], script, style, noscript, .ms-hide").ToList())
        {
            element.Remove();
        }

        foreach (var table in body.QuerySelectorAll("table#layoutsTable").ToList())
        {
            // a wiki page lays its zones out side by side in a table; as Markdown they are one after another
            var zones = document.CreateElement("div");
            foreach (var cell in table.QuerySelectorAll("td").ToList())
            {
                while (cell.FirstChild is { } child)
                {
                    zones.AppendChild(child);
                }
            }

            table.Replace(zones);
        }

        if (pageUrl is not null)
        {
            foreach (var (selector, attribute) in new[] { ("a[href]", "href"), ("img[src]", "src") })
            {
                foreach (var element in body.QuerySelectorAll(selector))
                {
                    var value = element.GetAttribute(attribute)?.Trim();
                    if (!string.IsNullOrEmpty(value) && !value.StartsWith('#') && Uri.TryCreate(pageUrl, value, out var absolute) && absolute.Scheme is "http" or "https")
                    {
                        element.SetAttribute(attribute, absolute.AbsoluteUri);
                    }
                }
            }
        }

        return body.InnerHtml;
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}

/// <summary>
/// The Markdown a listing made for each page, for the read that usually follows: a file system is told a file's size
/// before it reads it, so the size and the content come from the same conversion. Kept for a few hundred pages; a page
/// not here is fetched and converted again.
/// </summary>
internal sealed class PageCache
{
    private const int Capacity = 512;
    private readonly ConcurrentDictionary<string, (string Version, byte[] Markdown)> _pages = new(StringComparer.Ordinal);

    public void Remember(string id, string version, byte[] markdown)
    {
        if (_pages.Count >= Capacity)
        {
            _pages.Clear();
        }

        _pages[id] = (version, markdown);
    }

    public byte[]? Find(string id, string? version) => _pages.TryGetValue(id, out var page) && page.Version == version ? page.Markdown : null;
}
