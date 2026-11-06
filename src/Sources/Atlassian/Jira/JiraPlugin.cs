using System.Diagnostics.CodeAnalysis;
using AnyFS.Sdk;
using AnyFS.Sources.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Atlassian.Jira;

/// <summary>
/// Jira (type <c>jira</c>, folder <c>jira</c>, spec 7.10): a Jira Data Center server, a project, a saved filter, a board
/// or a query, read-only; a folder per issue with its fields, description and comments as Markdown and its attachments.
/// </summary>
public sealed class JiraPlugin : ISourcePlugin, IValidatable, ITestable, ISettingVisibility
{
    public string TypeId => "jira";

    public string DisplayName => "Jira";

    public string Description => "A Jira server (Data Center), project, saved filter or board: a folder per issue with its fields, description and comments as Markdown, and its attachments. Read-only.";

    public string TypeFolder => "jira";

    /// <summary>An issue card with a tick on a rounded square of Jira's blue, a PNG (Assets/jira.png); not Atlassian's logo.</summary>
    public Stream? OpenIcon() => typeof(JiraPlugin).Assembly.GetManifestResourceStream("jira.png");

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(JiraSettings);

    public object CreateDefaultSettings() => new JiraSettings();

    public ISource CreateSource(object settings) => new JiraSource((JiraSettings)settings);

    /// <summary>The server's host for a whole server; the project's key, the filter or board, and the host otherwise: <c>ABC (jira.example.com)</c>.</summary>
    public string? SuggestName(object settings)
    {
        if (((JiraSettings)settings).Url is not { } url)
        {
            return null;
        }

        // the installation is not known before the server is asked: the address's own segments name the scope
        var scope = AtlassianAddress.Candidates(url).Select(a => JiraScope.From(a, out _)).FirstOrDefault(s => s is not null && s.Kind != JiraScopeKind.Server);
        var name = scope?.Kind switch
        {
            JiraScopeKind.Project => $"{scope.Value} ({url.Host})",
            JiraScopeKind.Filter => $"filter {scope.Value} ({url.Host})",
            JiraScopeKind.Board => $"board {scope.Value} ({url.Host})",
            JiraScopeKind.Jql => $"query ({url.Host})",
            _ => url.Host,
        };
        var safe = AtlassianNames.Safe(name);
        return safe.Length == 0 ? null : safe;
    }

    /// <summary>The address is stored tidied: no fragment, no slash at the end of its path.</summary>
    public void NormaliseSettings(object settings)
    {
        var s = (JiraSettings)settings;
        s.Address = s.Url?.AbsoluteUri ?? (s.Address ?? "").Trim();
    }

    public bool IsVisible(object settings, string propertyName)
    {
        var s = (JiraSettings)settings;
        return propertyName switch
        {
            nameof(JiraSettings.AccessToken) => s.EffectiveSignIn == AtlassianSignIn.Token,
            nameof(JiraSettings.Username) or nameof(JiraSettings.Password) => s.EffectiveSignIn == AtlassianSignIn.Password,
            nameof(JiraSettings.BrowserPath) => s.EffectiveSignIn == AtlassianSignIn.WebSession,
            _ => true,
        };
    }

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (JiraSettings)settings;
        if (s.Url is null)
        {
            yield return new ValidationMessage("The address is an http or https address, like https://jira.example.com or https://jira.example.com/projects/ABC.", nameof(JiraSettings.Address));
        }

        foreach (var problem in AtlassianSignInChoices.Problems(s.SignInMethod, s, nameof(JiraSettings.SignInMethod), nameof(JiraSettings.AccessToken), nameof(JiraSettings.Username), nameof(JiraSettings.BrowserPath)))
        {
            yield return problem;
        }

        if (s.RefreshSeconds is < 0 or > 86_400)
        {
            yield return new ValidationMessage("Use Jira's answers for must be between 0 and 86400 seconds (a day).", nameof(JiraSettings.RefreshSeconds));
        }

        if (s.Concurrency is < 1 or > 64)
        {
            yield return new ValidationMessage("Requests at a time must be between 1 and 64.", nameof(JiraSettings.Concurrency));
        }
    }

    public Task<TestResult> TestAsync(object settings, IProgress<string> progress, CancellationToken ct) => TestAsync((JiraSettings)settings, progress, standIn: null, ct);

    /// <summary>Signs in the way the source will and asks Jira what the source would show: who is signed in, and what the address names.</summary>
    internal static async Task<TestResult> TestAsync(JiraSettings s, IProgress<string> progress, AtlassianStandIn? standIn, CancellationToken ct)
    {
        if (s.Url is null)
        {
            return new TestResult(false, "The address is not an http or https address.");
        }

        try
        {
            using var session = new AtlassianSession(AtlassianProduct.Jira, s, NullLogger.Instance, standIn);
            await session.SignInAsync(interactive: true, progress.Report, ct).ConfigureAwait(false);
            using var api = new AtlassianApi(session, s.Concurrency, NullLogger.Instance);
            var version = session.Version is null ? "" : $" (Jira {session.Version})";
            var who = $"Signed in to {session.Address.Base.Host}{version} as {session.User?.DisplayName} ({session.User?.Name}) {session.How}.";
            if (JiraScope.From(session.Address, out var problem) is not { } scope)
            {
                return new TestResult(false, $"{who} But {problem}");
            }

            progress.Report("Signed in; asking Jira what the address holds");
            switch (scope.Kind)
            {
                case JiraScopeKind.Server:
                    using (var projects = await api.GetAsync("project", ct).ConfigureAwait(false))
                    {
                        var count = projects.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array ? projects.RootElement.GetArrayLength() : 0;
                        return new TestResult(true, $"{who} The source shows the {count:N0} projects you can browse.");
                    }

                case JiraScopeKind.Project:
                    using (var project = await api.FindAsync($"project/{Uri.EscapeDataString(scope.Value)}", ct).ConfigureAwait(false))
                    {
                        return project is null
                            ? new TestResult(false, $"{who} But there is no project {scope.Value} that this account can browse.")
                            : new TestResult(true, $"{who} The source shows the project {project.RootElement.Str("name")} ({scope.Value}).");
                    }

                case JiraScopeKind.Filter:
                    using (var filter = await api.FindAsync($"filter/{scope.Value}", ct).ConfigureAwait(false))
                    {
                        return filter is null
                            ? new TestResult(false, $"{who} But there is no filter {scope.Value} that this account can see.")
                            : new TestResult(true, $"{who} The source shows the issues of the filter {filter.RootElement.Str("name")}.");
                    }

                default:
                    return new TestResult(true, $"{who} The source shows the issues the {(scope.Kind == JiraScopeKind.Board ? "board" : "query")} finds.");
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
