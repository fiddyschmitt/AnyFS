using System.Diagnostics.CodeAnalysis;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.GitLab;

/// <summary>
/// GitLab (type <c>gitlab</c>, folder <c>gitlab</c>, spec 7.8): a GitLab server, a group on it or a project, read-only;
/// each project's repository as it stands on its default branch, on every branch and at every tag, and the project's other
/// folders. GitLab.com and a server of one's own alike: they share one API.
/// </summary>
public sealed class GitLabPlugin : ISourcePlugin, IValidatable, ITestable, ISettingVisibility
{
    public string TypeId => "gitlab";

    public string DisplayName => "GitLab";

    public string Description => "A GitLab server, group or project (GitLab.com or your own): each project's repository on its default branch, on every branch and at every tag; its wiki, issues, merge requests, pipelines and releases. Read-only.";

    public string TypeFolder => "gitlab";

    /// <summary>A branch on a rounded square of GitLab's orange, a PNG (Assets/gitlab.png).</summary>
    public Stream? OpenIcon() => typeof(GitLabPlugin).Assembly.GetManifestResourceStream("gitlab.png");

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(GitLabSettings);

    public object CreateDefaultSettings() => new GitLabSettings();

    public ISource CreateSource(object settings) => new GitLabSource((GitLabSettings)settings);

    /// <summary>The server's host for a whole server; the group's or project's last name and the host otherwise: <c>app (gitlab.example.com)</c>.</summary>
    public string? SuggestName(object settings)
    {
        if (((GitLabSettings)settings).Url is not { } url)
        {
            return null;
        }

        var last = url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        var name = last is null ? url.Host : $"{Uri.UnescapeDataString(last)} ({url.Host})";
        var safe = string.Concat(name.Select(c => c < ' ' || "\\/:*?\"<>|".Contains(c) ? '-' : c)).Trim().Trim('.').Trim();
        return safe.Length == 0 ? null : safe;
    }

    /// <summary>The address is stored tidied: a page's address cut back to its project's, no <c>.git</c>, no slash at the end.</summary>
    public void NormaliseSettings(object settings)
    {
        var s = (GitLabSettings)settings;
        s.Address = s.Url?.AbsoluteUri.TrimEnd('/') ?? (s.Address ?? "").Trim();
    }

    public bool IsVisible(object settings, string propertyName)
    {
        var s = (GitLabSettings)settings;
        return propertyName switch
        {
            nameof(GitLabSettings.AccessToken) => s.EffectiveSignIn == GitLabSignIn.AccessToken,
            nameof(GitLabSettings.Username) or nameof(GitLabSettings.Password) => s.EffectiveSignIn == GitLabSignIn.Windows,
            nameof(GitLabSettings.BrowserPath) => s.EffectiveSignIn == GitLabSignIn.WebSession,
            nameof(GitLabSettings.OpenOnly) => s.IssuesAndMergeRequests,
            nameof(GitLabSettings.PipelinesKept) => s.Pipelines,
            _ => true,
        };
    }

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (GitLabSettings)settings;
        if (s.Url is null)
        {
            yield return new ValidationMessage("The address is an http or https address, like https://gitlab.example.com or https://gitlab.com/group/project.", nameof(GitLabSettings.Address));
        }

        if (GitLabSettings.SignInFrom(s.SignInMethod) is null)
        {
            yield return new ValidationMessage($"Sign in with: {GitLabSettings.AccessTokenChoice}.", nameof(GitLabSettings.SignInMethod));
        }

        if (s.EffectiveSignIn == GitLabSignIn.AccessToken && s.Token is null)
        {
            yield return new ValidationMessage("Give an access token: a personal, project or group access token with the read_api and read_repository scopes.", nameof(GitLabSettings.AccessToken));
        }

        if (s.PipelinesKept is < 1 or > 1000)
        {
            yield return new ValidationMessage("Pipelines kept must be between 1 and 1000.", nameof(GitLabSettings.PipelinesKept));
        }

        if (s.RefreshSeconds is < 5 or > 86_400)
        {
            yield return new ValidationMessage("Check for changes every must be between 5 seconds and 86400 (a day).", nameof(GitLabSettings.RefreshSeconds));
        }

        if (s.Concurrency is < 1 or > 64)
        {
            yield return new ValidationMessage("Requests at a time must be between 1 and 64.", nameof(GitLabSettings.Concurrency));
        }
    }

    public Task<TestResult> TestAsync(object settings, IProgress<string> progress, CancellationToken ct) => TestAsync((GitLabSettings)settings, progress, standIn: null, ct);

    /// <summary>
    /// Signs in the way the source will and asks GitLab what the source would show: who is signed in, and what the
    /// address names (a server's projects, a group's, a project's default branch).
    /// </summary>
    internal static async Task<TestResult> TestAsync(GitLabSettings s, IProgress<string> progress, GitLabStandIn? standIn, CancellationToken ct)
    {
        if (s.Url is null)
        {
            return new TestResult(false, "The address is not an http or https address.");
        }

        try
        {
            using var session = new GitLabSession(s, NullLogger.Instance, standIn);
            await session.SignInAsync(interactive: true, progress.Report, ct).ConfigureAwait(false);
            using var api = new GitLabApi(session, s.Concurrency, NullLogger.Instance);
            var who = $"Signed in to {session.Address.Server.Host} as {session.User?.Name} ({session.User?.Username}) {session.How}.";
            var path = session.Address.Path;
            progress.Report($"Signed in; asking GitLab what {(path.Length == 0 ? "the server" : path)} holds");
            if (path.Length == 0)
            {
                long projects = 0;
                await foreach (var _ in api.PagesAsync("projects?membership=true&simple=true&per_page=1", ct, total => projects = total).ConfigureAwait(false))
                {
                    break;
                }

                return new TestResult(true, $"{who} The source shows the groups and projects you are a member of ({projects:N0} projects).");
            }

            if (await api.FindAsync($"projects/{GitLabAddress.Id(path)}", ct).ConfigureAwait(false) is { } found)
            {
                using (found)
                {
                    var project = GlProject.Parse(found.RootElement);
                    var branch = project.EmptyRepo ? "an empty repository" : $"the default branch {project.DefaultBranch}";
                    return new TestResult(true, $"{who} The source shows the project {project.Name} ({project.FullPath}): {branch}.");
                }
            }

            if (await api.FindAsync($"groups/{GitLabAddress.Id(path)}?with_projects=false", ct).ConfigureAwait(false) is { } group)
            {
                using (group)
                {
                    var g = GlGroup.Parse(group.RootElement);
                    long projects = 0;
                    await foreach (var _ in api.PagesAsync($"groups/{g.Id}/projects?include_subgroups=true&simple=true&per_page=1", ct, total => projects = total).ConfigureAwait(false))
                    {
                        break;
                    }

                    return new TestResult(true, $"{who} The source shows the group {g.Name} ({g.FullPath}) and its subgroups: {projects:N0} projects.");
                }
            }

            return new TestResult(false, $"{who} But there is no group or project {path} that this account can see.");
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
