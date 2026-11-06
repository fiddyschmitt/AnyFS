using System.Diagnostics.CodeAnalysis;
using AnyFS.Sdk;
using AnyFS.Sources.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Atlassian.Bitbucket;

/// <summary>
/// Bitbucket (type <c>bitbucket</c>, folder <c>bitbucket</c>, spec 7.11): a Bitbucket Data Center server, a project or a
/// repository, read-only; each repository's files at its default branch, its branches and tags, and its pull requests as
/// Markdown with their diffs.
/// </summary>
public sealed class BitbucketPlugin : ISourcePlugin, IValidatable, ITestable, ISettingVisibility
{
    public string TypeId => "bitbucket";

    public string DisplayName => "Bitbucket";

    public string Description => "A Bitbucket server (Data Center), project or repository: each repository's files, its branches and tags, and its pull requests as Markdown with their diffs. Read-only.";

    public string TypeFolder => "bitbucket";

    /// <summary>Bitbucket's mark, a PNG (Assets/bitbucket.png).</summary>
    public Stream? OpenIcon() => typeof(BitbucketPlugin).Assembly.GetManifestResourceStream("bitbucket.png");

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(BitbucketSettings);

    public object CreateDefaultSettings() => new BitbucketSettings();

    public ISource CreateSource(object settings) => new BitbucketSource((BitbucketSettings)settings);

    /// <summary>The server's host for a whole server; the repository's slug or the project's key, and the host otherwise: <c>docs (bitbucket.example.com)</c>.</summary>
    public string? SuggestName(object settings)
    {
        if (((BitbucketSettings)settings).Url is not { } url)
        {
            return null;
        }

        // the installation is not known before the server is asked: the address's own segments name the scope
        var scope = AtlassianAddress.Candidates(url).Select(a => BitbucketScope.From(a, out _)).FirstOrDefault(s => s is not null && s.Kind != BitbucketScopeKind.Server);
        var name = scope?.Kind switch
        {
            BitbucketScopeKind.Repository => $"{scope.Slug} ({url.Host})",
            BitbucketScopeKind.Project => $"{scope.ProjectKey} ({url.Host})",
            _ => url.Host,
        };
        var safe = AtlassianNames.Safe(name);
        return safe.Length == 0 ? null : safe;
    }

    /// <summary>The address is stored tidied: no fragment, no slash at the end of its path.</summary>
    public void NormaliseSettings(object settings)
    {
        var s = (BitbucketSettings)settings;
        s.Address = s.Url?.AbsoluteUri ?? (s.Address ?? "").Trim();
    }

    public bool IsVisible(object settings, string propertyName)
    {
        var s = (BitbucketSettings)settings;
        return propertyName switch
        {
            nameof(BitbucketSettings.AccessToken) => s.EffectiveSignIn == AtlassianSignIn.Token,
            nameof(BitbucketSettings.Username) or nameof(BitbucketSettings.Password) => s.EffectiveSignIn == AtlassianSignIn.Password,
            nameof(BitbucketSettings.BrowserPath) => s.EffectiveSignIn == AtlassianSignIn.WebSession,
            nameof(BitbucketSettings.OpenOnly) => s.PullRequests,
            _ => true,
        };
    }

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (BitbucketSettings)settings;
        if (s.Url is null)
        {
            yield return new ValidationMessage("The address is an http or https address, like https://bitbucket.example.com or https://bitbucket.example.com/projects/ABC/repos/docs.", nameof(BitbucketSettings.Address));
        }

        foreach (var problem in AtlassianSignInChoices.Problems(s.SignInMethod, s, nameof(BitbucketSettings.SignInMethod), nameof(BitbucketSettings.AccessToken), nameof(BitbucketSettings.Username), nameof(BitbucketSettings.BrowserPath)))
        {
            yield return problem;
        }

        if (s.RefreshSeconds is < 0 or > 86_400)
        {
            yield return new ValidationMessage("Use Bitbucket's answers for must be between 0 and 86400 seconds (a day).", nameof(BitbucketSettings.RefreshSeconds));
        }

        if (s.RepositoriesAtATime is < 1 or > 64)
        {
            yield return new ValidationMessage("Repositories at a time must be between 1 and 64.", nameof(BitbucketSettings.RepositoriesAtATime));
        }

        if (s.RequestsPerRepository is < 1 or > 64)
        {
            yield return new ValidationMessage("Requests at a time per repository must be between 1 and 64.", nameof(BitbucketSettings.RequestsPerRepository));
        }
    }

    public Task<TestResult> TestAsync(object settings, IProgress<string> progress, CancellationToken ct) => TestAsync((BitbucketSettings)settings, progress, standIn: null, ct);

    /// <summary>Signs in the way the source will and asks Bitbucket what the source would show: who is signed in, and what the address names.</summary>
    internal static async Task<TestResult> TestAsync(BitbucketSettings s, IProgress<string> progress, AtlassianStandIn? standIn, CancellationToken ct)
    {
        if (s.Url is null)
        {
            return new TestResult(false, "The address is not an http or https address.");
        }

        try
        {
            using var session = new AtlassianSession(AtlassianProduct.Bitbucket, s, NullLogger.Instance, standIn);
            await session.SignInAsync(interactive: true, progress.Report, ct).ConfigureAwait(false);
            using var api = new AtlassianApi(session, s.Requests, NullLogger.Instance);
            var version = session.Version is null ? "" : $" (Bitbucket {session.Version})";
            var who = $"Signed in to {session.Address.Base.Host}{version} as {session.User?.DisplayName} ({session.User?.Name}) {session.How}.";
            if (BitbucketScope.From(session.Address, out var problem) is not { } scope)
            {
                return new TestResult(false, $"{who} But {problem}");
            }

            progress.Report("Signed in; asking Bitbucket what the address holds");
            switch (scope.Kind)
            {
                case BitbucketScopeKind.Server:
                    using (var projects = await api.GetAsync("projects?limit=1000", ct).ConfigureAwait(false))
                    {
                        var count = projects.RootElement.Arr("values").Count();
                        return new TestResult(true, $"{who} The source shows the {count:N0}{(projects.RootElement.Bool("isLastPage") ? "" : "+")} projects you can see.");
                    }

                case BitbucketScopeKind.Project:
                    using (var project = await api.FindAsync(BitbucketScope.ProjectPath(scope.ProjectKey), ct).ConfigureAwait(false))
                    {
                        return project is null
                            ? new TestResult(false, $"{who} But there is no project {scope.ProjectKey} that this account can see.")
                            : new TestResult(true, $"{who} The source shows the project {project.RootElement.Str("name")} ({scope.ProjectKey}).");
                    }

                default:
                    using (var repository = await api.FindAsync(BitbucketScope.RepositoryPath(scope.ProjectKey, scope.Slug), ct).ConfigureAwait(false))
                    {
                        return repository is null
                            ? new TestResult(false, $"{who} But there is no repository {scope.ProjectKey}/{scope.Slug} that this account can see.")
                            : new TestResult(true, $"{who} The source shows the repository {repository.RootElement.Str("name")} of {repository.RootElement.Obj("project").Str("name") ?? scope.ProjectKey}.");
                    }
            }
        }
        catch (SourceFailedException ex)
        {
            return new TestResult(false, ex.Message);
        }
        catch (VfsException ex)
        {
            return new TestResult(false, ex.Message);
        }
    }
}
