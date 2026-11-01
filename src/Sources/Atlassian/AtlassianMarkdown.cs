using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ReverseMarkdown;

namespace AnyFS.Sources.Atlassian;

/// <summary>
/// The Markdown the sources write (spec 7.10), as GitLab's and Outlook's: a title, a block of <c>**Label:** value</c>
/// lines, a rule, then the text, converted from HTML by ReverseMarkdown (as SharePoint's pages, Outlook's bodies and Teams'
/// messages are), its links rewritten where the source knows a better place for them (an attachment's file beside the
/// document). UTF-8 without a byte order mark, <c>\n</c> line ends.
/// </summary>
internal static partial class AtlassianMarkdown
{
    // the default flavour, as SharePoint's: tables, fenced code and nested lists the GitHub way, div blocks unwrapped
    private static readonly Lock ConverterGate = new();
    private static readonly Converter MarkdownConverter = new(new Config
    {
        Tags = { Unknown = Config.UnknownTagsOption.Bypass },
        Links = { SmartHref = true },
        Formatting = { RemoveComments = true, ListBulletChar = '-', OutputLineEnding = "\n" },
    });

    /// <summary>
    /// HTML as Markdown: each link's and image's address given to <paramref name="link"/> first (absolute, against
    /// <paramref name="baseUrl"/>), which may give another (a relative path to a file the source shows) or keep it.
    /// </summary>
    public static string FromHtml(string? html, Uri? baseUrl, Func<Uri, string?>? link = null)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "";
        }

        var document = new HtmlParser().ParseDocument("<!DOCTYPE html><html><body>" + html + "</body></html>");
        var body = document.Body!;
        foreach (var element in body.QuerySelectorAll("script, style, noscript").ToList())
        {
            element.Remove();
        }

        foreach (var (selector, attribute) in new[] { ("a[href]", "href"), ("img[src]", "src") })
        {
            foreach (var element in body.QuerySelectorAll(selector))
            {
                Relink(element, attribute, baseUrl, link);
            }
        }

        string markdown;
        lock (ConverterGate)
        {
            markdown = MarkdownConverter.Convert(body.InnerHtml);
        }

        return Tidy(markdown);
    }

    private static void Relink(IElement element, string attribute, Uri? baseUrl, Func<Uri, string?>? link)
    {
        var value = element.GetAttribute(attribute)?.Trim();
        if (string.IsNullOrEmpty(value) || value.StartsWith('#') || value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!(baseUrl is null ? Uri.TryCreate(value, UriKind.Absolute, out var absolute) : Uri.TryCreate(baseUrl, value, out absolute)) || absolute.Scheme is not ("http" or "https"))
        {
            return;
        }

        element.SetAttribute(attribute, link?.Invoke(absolute) ?? absolute.AbsoluteUri);
    }

    /// <summary>Converted text tidied: <c>\n</c> line ends, no run of blank lines, no white space at either end.</summary>
    public static string Tidy(string markdown) => BlankLines().Replace(markdown.Replace("\r\n", "\n").Replace('\r', '\n'), "\n\n").Trim();

    /// <summary>A <c>**Label:** value</c> line with a hard break, as GitLab's documents write them; nothing for no value.</summary>
    public static void Line(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sb.Append("**").Append(label).Append(":** ").Append(AtlassianNames.OneLine(value)).Append("  \n");
        }
    }

    /// <summary>A time as the documents write it, in this computer's time zone: <c>Fri 30 Oct 2026 14:02 (UTC+10:00)</c>, as GitLab's.</summary>
    public static string? Date(DateTimeOffset? time) => time?.ToLocalTime().ToString("ddd d MMM yyyy HH:mm (UTCzzz)", CultureInfo.InvariantCulture);

    /// <summary>A day as the documents write it: <c>Fri 30 Oct 2026</c>.</summary>
    public static string? Day(DateTimeOffset? time) => time?.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>A path a document beside a file links it by: each segment escaped as a Markdown link wants it, <c>/</c> between.</summary>
    public static string RelativeLink(params string[] segments) => string.Join('/', segments.Select(s => Uri.EscapeDataString(s)));

    /// <summary>The document's bytes: UTF-8 without a byte order mark, one line end at the end.</summary>
    public static byte[] Bytes(StringBuilder sb) => Encoding.UTF8.GetBytes(sb.ToString().TrimEnd('\n', ' ') + "\n");

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
