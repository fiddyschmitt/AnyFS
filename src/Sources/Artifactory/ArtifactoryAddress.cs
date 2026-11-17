namespace AnyFS.Sources.Artifactory;

/// <summary>What the address names (spec 7.13).</summary>
internal enum ArtifactoryScope
{
    /// <summary>The whole server: every repository the account can see.</summary>
    Server,

    /// <summary>One repository.</summary>
    Repository,

    /// <summary>A folder in a repository, and what is below it.</summary>
    Folder,
}

/// <summary>
/// The address the person gave, split: Artifactory's own address (where its REST API answers, <c>https://host/artifactory/</c>),
/// the repository's key and the folder's path in it, each empty where the address does not go that far. Which split is the
/// right one is known only once the server has answered (<see cref="Candidates"/>).
/// </summary>
/// <param name="Base">Artifactory's address, with a slash at the end.</param>
/// <param name="Repository">The repository's key as the address gives it; empty for the whole server.</param>
/// <param name="Path">The folder's path in the repository, as Artifactory writes it (<c>org/acme</c>); empty for the repository itself.</param>
internal sealed record ArtifactoryAddress(Uri Base, string Repository, string Path)
{
    // the package endpoints below /api whose next segment is a repository's key (api/npm/<repo>/...)
    private static readonly HashSet<string> PackageApis = new(StringComparer.OrdinalIgnoreCase)
    {
        "alpine", "ansible", "bower", "cargo", "chef", "composer", "conan", "conda", "cran", "deb", "docker", "gems", "go", "helm", "huggingfaceml",
        "lfs", "npm", "nuget", "oci", "opkg", "pods", "pub", "puppet", "pypi", "rpm", "swift", "terraform", "vagrant", "vcs",
    };

    public ArtifactoryScope Scope => Repository.Length == 0 ? ArtifactoryScope.Server : Path.Length == 0 ? ArtifactoryScope.Repository : ArtifactoryScope.Folder;

    /// <summary>A REST call's address (<paramref name="relative"/> to <c>api/</c>).</summary>
    public Uri Api(string relative) => new(Base, "api/" + relative);

    /// <summary>A file's own address: <c>&lt;base&gt;/&lt;repository&gt;/&lt;path&gt;</c>, each segment escaped.</summary>
    public Uri File(string repository, string path) => new(Base, Escaped(repository, path));

    /// <summary>A repository's key and a path in it as one path of an address, each segment escaped (a <c>+</c>, a space, a <c>#</c> or a <c>;</c> in a name kept as part of it).</summary>
    public static string Escaped(string repository, string path)
        => path.Length == 0 ? Uri.EscapeDataString(repository) : Uri.EscapeDataString(repository) + "/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>
    /// The address the person typed, tidied: trimmed, without a query or a slash at the end; the older web interface's address,
    /// which names the repository after a <c>#</c> (<c>…/artifactory/webapp/#/artifacts/browse/tree/General/repo/path</c>), as the
    /// repository's own. Null while it is no http(s) address with a host.
    /// </summary>
    public static Uri? Tidy(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || string.IsNullOrEmpty(url.Host))
        {
            return null;
        }

        var path = url.AbsolutePath.TrimEnd('/');
        const string webapp = "/webapp";
        const string browse = "#/artifacts/browse/";
        if (path.EndsWith(webapp, StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^webapp.Length];
            if (url.Fragment.StartsWith(browse, StringComparison.OrdinalIgnoreCase))
            {
                // tree/General/<repo>/<path>: the view (tree, simple) and the tab are not part of where it is
                var parts = url.Fragment[browse.Length..].Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 2)
                {
                    path += "/" + string.Join('/', parts[2..]);
                }
            }
        }

        return new Uri($"{url.Scheme}://{url.Authority}{path}");
    }

    /// <summary>
    /// Where Artifactory might be and what the rest of the address then names, likeliest first. An address with
    /// <c>artifactory</c> in its path has it there, the rest a repository and a path in it (or an API call's, cut back to its
    /// repository); the platform's own pages (<c>/ui/repos/tree/General/repo/path</c>, <c>/ui/native/repo/path</c>) have it
    /// beside them at <c>/artifactory</c>; an address that names neither has it at <c>/artifactory</c>, as JFrog installs it,
    /// at the host's root, as an old installation or a proxy in front of one may, or at a path of its own, which the address
    /// then begins with.
    /// </summary>
    public static IReadOnlyList<ArtifactoryAddress> Candidates(Uri tidy)
    {
        var origin = $"{tidy.Scheme}://{tidy.Authority}";
        var segments = tidy.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        var found = new List<ArtifactoryAddress>();
        void Add(IEnumerable<string> root, (string Repository, string Path) below)
        {
            var path = string.Join('/', root.Select(Uri.EscapeDataString));
            var candidate = new ArtifactoryAddress(new Uri(origin + (path.Length == 0 ? "/" : "/" + path + "/")), below.Repository, below.Path);
            if (!found.Contains(candidate))
            {
                found.Add(candidate);
            }
        }

        var artifactory = Array.FindIndex(segments, s => s.Equals("artifactory", StringComparison.OrdinalIgnoreCase));
        var ui = Array.FindIndex(segments, s => s.Equals("ui", StringComparison.OrdinalIgnoreCase));
        if (artifactory >= 0 && (ui < 0 || artifactory < ui))
        {
            Add(segments[..(artifactory + 1)], BelowArtifactory(segments[(artifactory + 1)..]));
        }
        else if (ui >= 0)
        {
            Add(segments[..ui].Append("artifactory"), BelowPages(segments[(ui + 1)..]));
        }

        // neither named, or what was named does not answer: where JFrog puts it, then the host's root
        var whole = segments.Length == 0 ? ("", "") : (segments[0], string.Join('/', segments[1..]));
        Add(["artifactory"], artifactory < 0 && ui < 0 ? whole : ("", ""));
        Add([], artifactory < 0 && ui < 0 ? whole : ("", ""));
        if (artifactory < 0 && ui < 0)
        {
            // a path of its own: each start of the address's path, the shortest first, the rest a repository and a path in it
            for (var i = 1; i <= segments.Length; i++)
            {
                Add(segments[..i], i == segments.Length ? ("", "") : (segments[i], string.Join('/', segments[(i + 1)..])));
            }
        }

        return found;
    }

    /// <summary>What the segments after <c>/artifactory/</c> name: a repository and a path in it, or an API call's repository.</summary>
    private static (string Repository, string Path) BelowArtifactory(string[] rest)
    {
        switch (rest)
        {
            case []:
            case ["webapp", ..]:
                return ("", "");
            case ["api", "storage", var repository, .. var path]:
                return (repository, string.Join('/', path));
            case ["api", var package, var repository, ..] when PackageApis.Contains(package):
                return (repository, "");
            case ["api", ..]:
                return ("", "");
            case ["list" or "simple", var repository, .. var path]:
                return (repository, string.Join('/', path));
            default:
                return (rest[0], string.Join('/', rest[1..]));
        }
    }

    /// <summary>What the segments after the platform's <c>/ui/</c> name: the repository and path its tree or its plain listing shows; nothing for any other page.</summary>
    private static (string Repository, string Path) BelowPages(string[] rest) => rest switch
    {
        ["repos", "tree", _, var repository, .. var path] => (repository, string.Join('/', path)),
        ["native", var repository, .. var path] => (repository, string.Join('/', path)),
        _ => ("", ""),
    };
}
