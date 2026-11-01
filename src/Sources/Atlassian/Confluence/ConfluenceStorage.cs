using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AnyFS.Sources.Atlassian.Confluence;

/// <summary>What converting a page's storage format needs to know of the source: where links and images go, who people are.</summary>
/// <param name="Installation">The installation's address (with a slash at the end): links to pages and spaces go there.</param>
/// <param name="SpaceKey">The page's own space: a link that names no space means it.</param>
/// <param name="Attachment">A file attached to the page itself, by its name: the path its file shows at beside the Markdown, or null to link it in Confluence.</param>
/// <param name="PageId">The page's id, for an attachment's address in Confluence.</param>
/// <param name="User">A person by their user key or name: the name to show, or null where unknown.</param>
internal sealed record StorageContext(Uri Installation, string SpaceKey, string PageId, Func<string, string?> Attachment, Func<string, string?> User);

/// <summary>
/// A page's storage format (Confluence's own XHTML: HTML with <c>ac:</c> macros and <c>ri:</c> resource identifiers) as
/// plain HTML for the Markdown converter (spec 7.10): code and noformat as code blocks; info, tip, note, warning and panel
/// as quotes under their kind; expand as its body under its title; status as <c>[TEXT]</c>; task lists as check lists;
/// links to pages, blog posts and spaces at their addresses in Confluence, to the page's own attachments at their files,
/// to people as their names; images likewise; macros that show other content (a table of contents, the child pages, an
/// included page) as a line naming what they show; any other macro its own text. Parsed as XML (the HTML entities it uses
/// turned into numbers first); what cannot be parsed is kept as its text.
/// </summary>
internal static partial class ConfluenceStorage
{
    private static readonly XNamespace Ac = "urn:anyfs:ac";
    private static readonly XNamespace Ri = "urn:anyfs:ri";

    private static readonly HashSet<string> KeptHtml = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "table", "thead", "tbody", "tfoot", "tr", "th", "td", "a", "strong", "b",
        "em", "i", "u", "s", "del", "strike", "code", "pre", "blockquote", "br", "hr", "img", "sub", "sup", "span", "div", "dl", "dt", "dd",
        "caption", "colgroup", "col", "small", "big", "cite", "q", "abbr", "kbd", "samp", "var", "mark", "figure", "figcaption",
    };

    private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase) { "br", "hr", "img", "col" };

    // the emoticons Confluence has always had, as the emoji they show
    private static readonly Dictionary<string, string> Emoticons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["smile"] = "🙂", ["sad"] = "🙁", ["cheeky"] = "😛", ["laugh"] = "😄", ["wink"] = "😉", ["thumbs-up"] = "👍",
        ["thumbs-down"] = "👎", ["information"] = "ℹ️", ["tick"] = "✅", ["cross"] = "❌", ["warning"] = "⚠️", ["plus"] = "➕",
        ["minus"] = "➖", ["question"] = "❓", ["light-on"] = "💡", ["light-off"] = "💡", ["yellow-star"] = "⭐", ["red-star"] = "⭐",
        ["green-star"] = "⭐", ["blue-star"] = "⭐", ["heart"] = "❤️", ["broken-heart"] = "💔",
    };

    // macros that show other content than the page's own: what a line says in their place
    private static readonly Dictionary<string, string> Shows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["toc"] = "table of contents", ["toc-zone"] = "table of contents", ["children"] = "child pages", ["pagetree"] = "page tree",
        ["pagetreesearch"] = "page tree search", ["recently-updated"] = "recently updated pages", ["contentbylabel"] = "pages with labels",
        ["content-report-table"] = "pages with labels", ["blog-posts"] = "blog posts", ["attachments"] = "the page's attachments",
        ["gallery"] = "image gallery", ["detailssummary"] = "page properties report", ["tasks-report-macro"] = "task report",
        ["listlabels"] = "labels", ["popular-labels"] = "popular labels", ["related-labels"] = "related labels", ["navmap"] = "navigation map",
        ["contributors"] = "contributors", ["contributors-summary"] = "contributors", ["spaces"] = "spaces", ["create-from-template"] = "a create button",
    };

    private static readonly Dictionary<string, string> Callouts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["info"] = "Info", ["tip"] = "Tip", ["note"] = "Note", ["warning"] = "Warning", ["panel"] = "Panel", ["success"] = "Success", ["error"] = "Error",
    };

    /// <summary>The storage format as HTML for the Markdown converter.</summary>
    public static string ToHtml(string? storage, StorageContext context)
    {
        if (string.IsNullOrWhiteSpace(storage))
        {
            return "";
        }

        XElement root;
        try
        {
            var xml = $"<root xmlns:ac=\"{Ac.NamespaceName}\" xmlns:ri=\"{Ri.NamespaceName}\" xmlns:at=\"urn:anyfs:at\">{NumericEntities(storage)}</root>";
            root = XElement.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            // not well formed: its text, its tags gone
            return "<p>" + WebUtility.HtmlEncode(WebUtility.HtmlDecode(Tags().Replace(storage, " "))) + "</p>";
        }

        var html = new StringBuilder(storage.Length);
        foreach (var node in root.Nodes())
        {
            Write(node, html, context);
        }

        return html.ToString();
    }

    /// <summary>
    /// HTML's named entities (<c>&amp;nbsp;</c>, <c>&amp;mdash;</c>) as numeric ones, which XML knows; XML's own five as
    /// they are; an unknown name as text. A CDATA section (a code macro's body) is literal text, entities and all, and
    /// is left as it is.
    /// </summary>
    internal static string NumericEntities(string text) => NamedEntity().Replace(text, m =>
    {
        if (!m.Groups[1].Success)
        {
            return m.Value;
        }

        var name = m.Groups[1].Value;
        if (name is "lt" or "gt" or "amp" or "quot" or "apos")
        {
            return m.Value;
        }

        var decoded = WebUtility.HtmlDecode(m.Value);
        return decoded == m.Value ? "&amp;" + name + ";" : string.Concat(decoded.EnumerateRunes().Select(r => $"&#{r.Value};"));
    });

    [GeneratedRegex(@"<!\[CDATA\[.*?\]\]>|&([A-Za-z][A-Za-z0-9]*);", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex NamedEntity();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();

    private static void Write(XNode node, StringBuilder html, StorageContext context)
    {
        switch (node)
        {
            case XCData cdata:
                html.Append(WebUtility.HtmlEncode(cdata.Value));
                break;
            case XText text:
                html.Append(WebUtility.HtmlEncode(text.Value));
                break;
            case XElement element when element.Name.Namespace == Ac:
                WriteAc(element, html, context);
                break;
            case XElement element when element.Name.Namespace == Ri:
                // a resource outside a link or image: its name, where it has one
                html.Append(WebUtility.HtmlEncode(ResourceText(element, context) ?? ""));
                break;
            case XElement element when element.Name.Namespace == XNamespace.None:
                WriteHtml(element, html, context);
                break;
            case XElement element:
                WriteChildren(element, html, context);
                break;
        }
    }

    private static void WriteChildren(XElement element, StringBuilder html, StorageContext context)
    {
        foreach (var child in element.Nodes())
        {
            Write(child, html, context);
        }
    }

    private static void WriteHtml(XElement element, StringBuilder html, StorageContext context)
    {
        var name = element.Name.LocalName;
        if (name.Equals("time", StringComparison.OrdinalIgnoreCase))
        {
            html.Append(WebUtility.HtmlEncode((string?)element.Attribute("datetime") ?? element.Value));
            return;
        }

        if (!KeptHtml.Contains(name))
        {
            WriteChildren(element, html, context);
            return;
        }

        html.Append('<').Append(name);
        foreach (var attribute in element.Attributes().Where(a => a.Name.Namespace == XNamespace.None && a.Name.LocalName is "href" or "src" or "colspan" or "rowspan" or "alt" or "title"))
        {
            var value = attribute.Value;
            if (attribute.Name.LocalName is "href" or "src" && Uri.TryCreate(context.Installation, value, out var absolute) && absolute.Scheme is "http" or "https")
            {
                value = absolute.AbsoluteUri;
            }

            html.Append(' ').Append(attribute.Name.LocalName).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');
        }

        if (Void.Contains(name))
        {
            html.Append(" />");
            return;
        }

        html.Append('>');
        WriteChildren(element, html, context);
        html.Append("</").Append(name).Append('>');
    }

    private static void WriteAc(XElement element, StringBuilder html, StorageContext context)
    {
        switch (element.Name.LocalName)
        {
            case "structured-macro":
            case "macro":
                WriteMacro(element, html, context);
                break;
            case "link":
                WriteLink(element, html, context);
                break;
            case "image":
                WriteImage(element, html, context);
                break;
            case "emoticon":
                var fallback = (string?)element.Attribute(Ac + "emoji-fallback");
                var emoticon = (string?)element.Attribute(Ac + "name") ?? "";
                html.Append(WebUtility.HtmlEncode(fallback is { Length: > 0 } && !fallback.StartsWith(':') ? fallback : Emoticons.GetValueOrDefault(emoticon, $":{emoticon}:")));
                break;
            case "task-list":
                html.Append("<ul>");
                foreach (var task in element.Elements(Ac + "task"))
                {
                    var done = string.Equals(task.Element(Ac + "task-status")?.Value.Trim(), "complete", StringComparison.OrdinalIgnoreCase);
                    html.Append("<li>").Append(done ? "[x] " : "[ ] ");
                    if (task.Element(Ac + "task-body") is { } body)
                    {
                        WriteChildren(body, html, context);
                    }

                    html.Append("</li>");
                }

                html.Append("</ul>");
                break;
            case "placeholder":
            case "parameter":
            case "task-id":
            case "task-status":
                break;
            default:
                // layouts and their sections and cells, inline comment markers, rich bodies met on their own...: their content
                WriteChildren(element, html, context);
                break;
        }
    }

    private static void WriteMacro(XElement macro, StringBuilder html, StorageContext context)
    {
        var name = (string?)macro.Attribute(Ac + "name") ?? "";
        var parameters = macro.Elements(Ac + "parameter").GroupBy(p => (string?)p.Attribute(Ac + "name") ?? "").ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        string? Parameter(string key) => parameters.TryGetValue(key, out var p) ? (p.HasElements ? ResourceText(p.Elements().First(), context) : p.Value.Trim()) : null;
        var rich = macro.Element(Ac + "rich-text-body");
        var plain = macro.Element(Ac + "plain-text-body")?.Value;

        if (name is "code" or "noformat" or "code-block")
        {
            if (Parameter("title") is { Length: > 0 } title)
            {
                html.Append("<p><strong>").Append(WebUtility.HtmlEncode(title)).Append("</strong></p>");
            }

            var language = Parameter("language");
            html.Append("<pre><code").Append(language is { Length: > 0 } ? $" class=\"language-{WebUtility.HtmlEncode(language)}\"" : "").Append('>');
            html.Append(WebUtility.HtmlEncode(plain ?? rich?.Value ?? "")).Append("</code></pre>");
            return;
        }

        if (Callouts.TryGetValue(name, out var kind))
        {
            var title = Parameter("title");
            html.Append("<blockquote><p><strong>").Append(WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(title) ? kind : $"{kind}: {title}")).Append("</strong></p>");
            if (rich is not null)
            {
                WriteChildren(rich, html, context);
            }

            html.Append("</blockquote>");
            return;
        }

        switch (name)
        {
            case "expand":
                html.Append("<p><strong>").Append(WebUtility.HtmlEncode(Parameter("title") is { Length: > 0 } t ? t : "Expand")).Append("</strong></p>");
                if (rich is not null)
                {
                    WriteChildren(rich, html, context);
                }

                return;
            case "status":
                html.Append('[').Append(WebUtility.HtmlEncode(Parameter("title") is { Length: > 0 } s ? s : Parameter("colour") ?? "status")).Append(']');
                return;
            case "jira":
                html.Append(WebUtility.HtmlEncode(Parameter("key") ?? (Parameter("jqlQuery") is { } jql ? $"(Jira issues: {jql})" : "(Jira issues)")));
                return;
            case "anchor":
            case "livesearch":
            case "html":
            case "html-include":
                return;
            case "include":
            case "excerpt-include":
                var included = macro.Elements(Ac + "parameter").SelectMany(p => p.Elements()).FirstOrDefault();
                var what = name == "include" ? "includes the page" : "includes the excerpt of";
                html.Append("<p><em>(").Append(what).Append(' ');
                if (included is not null && LinkTarget(included, context) is { } target)
                {
                    html.Append("<a href=\"").Append(WebUtility.HtmlEncode(target.Url)).Append("\">").Append(WebUtility.HtmlEncode(target.Text)).Append("</a>");
                }
                else
                {
                    html.Append(WebUtility.HtmlEncode(Parameter("") ?? "another page"));
                }

                html.Append(")</em></p>");
                return;
            case "view-file":
            case "viewpdf":
            case "viewdoc":
            case "viewppt":
            case "viewxls":
            case "multimedia":
            case "widget":
                var resource = macro.Elements(Ac + "parameter").SelectMany(p => p.Elements()).FirstOrDefault();
                if (resource is not null && LinkTarget(resource, context) is { } file)
                {
                    html.Append("<p><a href=\"").Append(WebUtility.HtmlEncode(file.Url)).Append("\">").Append(WebUtility.HtmlEncode(file.Text)).Append("</a></p>");
                }
                else if (Parameter("url") is { Length: > 0 } url)
                {
                    html.Append("<p><a href=\"").Append(WebUtility.HtmlEncode(url)).Append("\">").Append(WebUtility.HtmlEncode(url)).Append("</a></p>");
                }

                return;
            case "profile":
            case "profile-picture":
                // the user parameter is a ri:user, which Parameter already names with its @; a plain name gets one
                html.Append(WebUtility.HtmlEncode(Parameter("user") is { Length: > 0 } user ? (user.StartsWith('@') ? user : "@" + user) : ""));
                return;
        }

        if (Shows.TryGetValue(name, out var shows))
        {
            html.Append("<p><em>(").Append(WebUtility.HtmlEncode(shows)).Append(")</em></p>");
            return;
        }

        // any other macro (excerpt, details, section, column, a third party's): its own text
        if (rich is not null)
        {
            WriteChildren(rich, html, context);
        }
        else if (!string.IsNullOrWhiteSpace(plain))
        {
            html.Append("<p>").Append(WebUtility.HtmlEncode(plain)).Append("</p>");
        }
    }

    private static void WriteLink(XElement link, StringBuilder html, StorageContext context)
    {
        var resource = link.Elements().FirstOrDefault(e => e.Name.Namespace == Ri);
        var anchor = (string?)link.Attribute(Ac + "anchor");
        var plainBody = link.Element(Ac + "plain-text-link-body")?.Value;
        var richBody = link.Element(Ac + "link-body");

        if (resource is not null && resource.Name.LocalName == "user")
        {
            html.Append(WebUtility.HtmlEncode("@" + (UserName(resource, context) ?? "someone")));
            return;
        }

        var target = resource is null ? null : LinkTarget(resource, context);
        var url = target?.Url ?? "";
        if (anchor is { Length: > 0 })
        {
            url = url.Length == 0 ? "#" + anchor : url + "#" + anchor;
        }

        html.Append("<a href=\"").Append(WebUtility.HtmlEncode(url)).Append("\">");
        if (richBody is not null && richBody.Nodes().Any())
        {
            WriteChildren(richBody, html, context);
        }
        else
        {
            html.Append(WebUtility.HtmlEncode(!string.IsNullOrWhiteSpace(plainBody) ? plainBody : target?.Text ?? anchor ?? url));
        }

        html.Append("</a>");
    }

    private static void WriteImage(XElement image, StringBuilder html, StorageContext context)
    {
        var resource = image.Elements().FirstOrDefault(e => e.Name.Namespace == Ri);
        var target = resource is null ? null : LinkTarget(resource, context);
        if (target is null)
        {
            return;
        }

        var alt = (string?)image.Attribute(Ac + "alt") ?? (string?)image.Attribute(Ac + "title") ?? target.Text;
        html.Append("<img src=\"").Append(WebUtility.HtmlEncode(target.Url)).Append("\" alt=\"").Append(WebUtility.HtmlEncode(alt)).Append("\" />");
    }

    private sealed record Target(string Url, string Text);

    /// <summary>Where a resource identifier leads, and what a link to it says when it says nothing of its own.</summary>
    private static Target? LinkTarget(XElement resource, StorageContext context)
    {
        string? A(string name) => (string?)resource.Attribute(Ri + name);
        var space = A("space-key") is { Length: > 0 } key ? key : context.SpaceKey;
        switch (resource.Name.LocalName)
        {
            case "page":
                var title = A("content-title") ?? "";
                return new Target(PageUrl(context.Installation, space, title), title);
            case "blog-post":
                var post = A("content-title") ?? "";
                var day = A("posting-day")?.Replace('-', '/') ?? "";
                return new Target(new Uri(context.Installation, $"display/{Uri.EscapeDataString(space)}/{day}/{TitlePath(post)}".Replace("//", "/")).AbsoluteUri, post);
            case "space":
                return new Target(new Uri(context.Installation, $"display/{Uri.EscapeDataString(space)}").AbsoluteUri, space);
            case "attachment":
                var file = A("filename") ?? "";
                var owner = resource.Elements().FirstOrDefault(e => e.Name.Namespace == Ri && e.Name.LocalName is "page" or "blog-post");
                if (owner is null && context.Attachment(file) is { } local)
                {
                    return new Target(local, file);
                }

                if (owner is not null)
                {
                    var ownerSpace = (string?)owner.Attribute(Ri + "space-key") is { Length: > 0 } s ? s : context.SpaceKey;
                    return new Target(PageUrl(context.Installation, ownerSpace, (string?)owner.Attribute(Ri + "content-title") ?? ""), file);
                }

                return new Target(new Uri(context.Installation, $"download/attachments/{context.PageId}/{Uri.EscapeDataString(file)}").AbsoluteUri, file);
            case "url":
                var value = A("value") ?? "";
                return new Target(value, value);
            case "user":
                return new Target("", "@" + (UserName(resource, context) ?? "someone"));
            case "content-entity":
                var id = A("content-id") ?? "";
                return new Target(new Uri(context.Installation, $"pages/viewpage.action?pageId={Uri.EscapeDataString(id)}").AbsoluteUri, id);
            case "shortcut":
                var shortcut = $"{A("key")}@{A("parameter")}";
                return new Target(shortcut, shortcut);
            default:
                return null;
        }
    }

    private static string? ResourceText(XElement resource, StorageContext context)
        => resource.Name.LocalName == "user" ? "@" + (UserName(resource, context) ?? "someone") : LinkTarget(resource, context)?.Text;

    private static string? UserName(XElement user, StorageContext context)
        => ((string?)user.Attribute(Ri + "userkey") ?? (string?)user.Attribute(Ri + "username")) is { Length: > 0 } who ? context.User(who) ?? who : null;

    /// <summary>A page's address in Confluence by its space and title: <c>display/KEY/Page+Title</c>.</summary>
    public static string PageUrl(Uri installation, string space, string title)
        => new Uri(installation, $"display/{Uri.EscapeDataString(space)}/{TitlePath(title)}").AbsoluteUri;

    private static string TitlePath(string title) => string.Join('+', title.Split(' ').Select(Uri.EscapeDataString));

    /// <summary>The user keys and names the storage format mentions (the people a page's Markdown names), each once.</summary>
    public static IEnumerable<string> Mentioned(string? storage)
        => string.IsNullOrEmpty(storage) ? [] : UserReference().Matches(storage).Select(m => m.Groups[2].Value).Distinct(StringComparer.Ordinal);

    [GeneratedRegex(@"<ri:user\s+ri:(userkey|username)=""([^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex UserReference();
}
