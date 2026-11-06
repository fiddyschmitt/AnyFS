namespace AnyFS.Sources.Atlassian;

/// <summary>The products the assembly's sources read (Data Center; spec 7.10, 7.11, 7.12).</summary>
internal enum AtlassianProduct
{
    Jira,
    Confluence,
    Bitbucket,
    Bamboo,
}

internal static class AtlassianProducts
{
    /// <summary>The product's name, as messages say it.</summary>
    public static string Name(this AtlassianProduct product) => product switch
    {
        AtlassianProduct.Jira => "Jira",
        AtlassianProduct.Confluence => "Confluence",
        AtlassianProduct.Bitbucket => "Bitbucket",
        _ => "Bamboo",
    };

    /// <summary>The source type's name, as log lines start and settings folders are named: <c>jira</c>, <c>confluence</c>, <c>bitbucket</c>, <c>bamboo</c>.</summary>
    public static string TypeId(this AtlassianProduct product) => product switch
    {
        AtlassianProduct.Jira => "jira",
        AtlassianProduct.Confluence => "confluence",
        AtlassianProduct.Bitbucket => "bitbucket",
        _ => "bamboo",
    };

    /// <summary>The product an application links manifest names (<c>typeId</c>): Bitbucket Server still calls itself <c>stash</c> there.</summary>
    public static AtlassianProduct? OfManifest(string? typeId) => (typeId ?? "").ToLowerInvariant() switch
    {
        "jira" => AtlassianProduct.Jira,
        "confluence" => AtlassianProduct.Confluence,
        "stash" or "bitbucket" => AtlassianProduct.Bitbucket,
        "bamboo" => AtlassianProduct.Bamboo,
        _ => null,
    };

    /// <summary>The name of the environment variable that moves the web session's browser profiles (tests), <c>ANYFS_JIRA_PROFILES</c>, and the stem of the others.</summary>
    public static string Variable(this AtlassianProduct product, string suffix) => "ANYFS_" + product.TypeId().ToUpperInvariant() + "_" + suffix;
}

/// <summary>
/// The address the person gave, split once the installation is found (spec 7.10): the installation's own address (the
/// host, or a path under it for one installed at a context path, <c>https://example.com/jira</c>) and the rest of the
/// address below it, path and query, which names the source's scope (a project, a space, a page...).
/// </summary>
/// <param name="Base">The installation's address, with a slash at the end (<c>https://example.com/jira/</c>).</param>
/// <param name="Rest">The path below it, its segments unescaped (<c>browse/ABC-123</c>); empty for the installation itself.</param>
/// <param name="Query">The address's query, unescaped, by name in either case.</param>
internal sealed record AtlassianAddress(Uri Base, IReadOnlyList<string> Rest, IReadOnlyDictionary<string, string> Query)
{
    /// <summary>The installation's address without the slash at the end, as people write it.</summary>
    public string Server => Base.AbsoluteUri.TrimEnd('/');

    /// <summary>A page of the installation (<c>login.jsp</c>), relative to its address.</summary>
    public Uri Page(string relative) => new(Base, relative.TrimStart('/'));

    /// <summary>The product's REST API: Jira's <c>rest/api/2/</c>, Confluence's <c>rest/api/</c>, Bitbucket's <c>rest/api/1.0/</c>, Bamboo's <c>rest/api/latest/</c>, with a slash at the end.</summary>
    public Uri Api(AtlassianProduct product) => new(Base, product switch
    {
        AtlassianProduct.Jira => "rest/api/2/",
        AtlassianProduct.Confluence => "rest/api/",
        AtlassianProduct.Bitbucket => "rest/api/1.0/",
        _ => "rest/api/latest/",
    });

    /// <summary>
    /// The address the person typed, tidied: trimmed, its fragment dropped, its query kept (a filter's id, a page's id).
    /// Null while it is no http(s) address with a host.
    /// </summary>
    public static Uri? Tidy(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || string.IsNullOrEmpty(url.Host))
        {
            return null;
        }

        var path = url.AbsolutePath.TrimEnd('/');
        var query = url.Query.Length > 1 ? url.Query : "";
        return new Uri($"{url.Scheme}://{url.Authority}{path}{query}");
    }

    /// <summary>
    /// Where the installation might be, nearest first: the host itself, then the address cut after each of its path's
    /// segments (an installation at a context path). The rest of the address is then the scope's.
    /// </summary>
    public static IEnumerable<AtlassianAddress> Candidates(Uri tidy)
    {
        var segments = tidy.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = ParseQuery(tidy.Query);
        for (var i = 0; i <= segments.Length; i++)
        {
            var root = $"{tidy.Scheme}://{tidy.Authority}/" + (i == 0 ? "" : string.Join('/', segments[..i]) + "/");
            yield return new AtlassianAddress(new Uri(root), segments[i..].Select(Unescape).ToList(), query);
        }
    }

    /// <summary>A query string's values by name (the first of a name given twice), unescaped, <c>+</c> as a space.</summary>
    public static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var name = Unescape(eq < 0 ? part : part[..eq]);
            var value = eq < 0 ? "" : Unescape(part[(eq + 1)..]);
            values.TryAdd(name, value);
        }

        return values;
    }

    /// <summary>A path segment or query value as the person meant it: <c>+</c> is a space in both products' addresses.</summary>
    private static string Unescape(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));
}
