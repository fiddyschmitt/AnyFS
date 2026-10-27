namespace AnyFS.Sources.GitLab;

/// <summary>What the address names, once the server has been asked (spec 7.8).</summary>
internal enum ScopeKind
{
    /// <summary>The whole server: the groups and projects the account is a member of.</summary>
    Server,

    /// <summary>A group (or subgroup): its subgroups and projects.</summary>
    Group,

    /// <summary>One project: its folders only.</summary>
    Project,
}

/// <summary>
/// The address the person gave, split once the server is found: the GitLab installation's own address (where its
/// <c>/api/v4</c> is: the host, or a path under it for one installed at a relative URL) and the path of the group or
/// project below it, empty for the whole server.
/// </summary>
/// <param name="Server">The installation's address, with no slash at the end (<c>https://gitlab.example.com</c>).</param>
/// <param name="Path">The group's or project's full path (<c>group/subgroup/project</c>); empty for the whole server.</param>
internal sealed record GitLabAddress(Uri Server, string Path)
{
    /// <summary>The REST API's address, with a slash at the end.</summary>
    public Uri Api => new(Server.AbsoluteUri.TrimEnd('/') + "/api/v4/");

    /// <summary>The GraphQL endpoint.</summary>
    public Uri GraphQl => new(Server.AbsoluteUri.TrimEnd('/') + "/api/graphql");

    /// <summary>A page of the installation (<c>users/sign_in</c>), relative to its address.</summary>
    public Uri Page(string relative) => new(Server.AbsoluteUri.TrimEnd('/') + "/" + relative.TrimStart('/'));

    /// <summary>
    /// The address the person typed, tidied: trimmed, without a query or fragment, a page of a project cut back to the
    /// project (GitLab puts its pages after <c>/-/</c>), a clone address's <c>.git</c> dropped, no slash at the end. Null
    /// while it is no http(s) address with a host.
    /// </summary>
    public static Uri? Tidy(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || string.IsNullOrEmpty(url.Host))
        {
            return null;
        }

        var path = url.AbsolutePath;
        var pages = path.IndexOf("/-/", StringComparison.Ordinal);
        if (pages >= 0)
        {
            path = path[..pages];
        }

        path = path.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        return new Uri($"{url.Scheme}://{url.Authority}{path}");
    }

    /// <summary>
    /// Where the installation might be, nearest first: the host itself, then the address cut after each of its path's
    /// segments but the last (an installation at a relative URL, <c>https://example.com/gitlab</c>). The rest of the address
    /// is then the group's or project's path.
    /// </summary>
    public static IEnumerable<GitLabAddress> Candidates(Uri tidy)
    {
        var segments = tidy.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i <= segments.Length; i++)
        {
            var root = $"{tidy.Scheme}://{tidy.Authority}" + (i == 0 ? "" : "/" + string.Join('/', segments[..i]));
            yield return new GitLabAddress(new Uri(root), Uri.UnescapeDataString(string.Join('/', segments[i..])));
        }
    }

    /// <summary>A path's segments' URL-encoded form for the API's <c>:id</c> (<c>group%2Fproject</c>).</summary>
    public static string Id(string fullPath) => Uri.EscapeDataString(fullPath);
}
