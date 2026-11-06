using System.Diagnostics.CodeAnalysis;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Atlassian.Bamboo;

/// <summary>
/// Bamboo (type <c>bamboo</c>, folder <c>bamboo</c>, spec 7.12): a Bamboo Data Center server, a project or a plan,
/// read-only; each plan's builds as Markdown, with the jobs' logs and the artifacts they kept.
/// </summary>
public sealed class BambooPlugin : ISourcePlugin, IValidatable, ITestable, ISettingVisibility
{
    public string TypeId => "bamboo";

    public string DisplayName => "Bamboo";

    public string Description => "A Bamboo server (Data Center), project or plan: each plan's builds as Markdown, with the jobs' logs and the artifacts they kept. Read-only.";

    public string TypeFolder => "bamboo";

    /// <summary>Bamboo's mark, a PNG (Assets/bamboo.png).</summary>
    public Stream? OpenIcon() => typeof(BambooPlugin).Assembly.GetManifestResourceStream("bamboo.png");

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(BambooSettings);

    public object CreateDefaultSettings() => new BambooSettings();

    public ISource CreateSource(object settings) => new BambooSource((BambooSettings)settings);

    /// <summary>The server's host for a whole server; the plan's or the project's key, and the host otherwise: <c>PROJ-PLAN (bamboo.example.com)</c>.</summary>
    public string? SuggestName(object settings)
    {
        if (((BambooSettings)settings).Url is not { } url)
        {
            return null;
        }

        // the installation is not known before the server is asked: the address's own segments name the scope
        var scope = AtlassianAddress.Candidates(url).Select(a => BambooScope.From(a, out _)).FirstOrDefault(s => s is not null && s.Kind != BambooScopeKind.Server);
        var name = scope?.Kind switch
        {
            BambooScopeKind.Plan => $"{scope.PlanKey} ({url.Host})",
            BambooScopeKind.Project => $"{scope.ProjectKey} ({url.Host})",
            _ => url.Host,
        };
        var safe = AtlassianNames.Safe(name);
        return safe.Length == 0 ? null : safe;
    }

    /// <summary>The address is stored tidied: no fragment, no slash at the end of its path.</summary>
    public void NormaliseSettings(object settings)
    {
        var s = (BambooSettings)settings;
        s.Address = s.Url?.AbsoluteUri ?? (s.Address ?? "").Trim();
    }

    public bool IsVisible(object settings, string propertyName)
    {
        var s = (BambooSettings)settings;
        return propertyName switch
        {
            nameof(BambooSettings.AccessToken) => s.EffectiveSignIn == AtlassianSignIn.Token,
            nameof(BambooSettings.Username) or nameof(BambooSettings.Password) => s.EffectiveSignIn == AtlassianSignIn.Password,
            nameof(BambooSettings.BrowserPath) => s.EffectiveSignIn == AtlassianSignIn.WebSession,
            _ => true,
        };
    }

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (BambooSettings)settings;
        if (s.Url is null)
        {
            yield return new ValidationMessage("The address is an http or https address, like https://bamboo.example.com or https://bamboo.example.com/browse/PROJ-PLAN.", nameof(BambooSettings.Address));
        }

        foreach (var problem in AtlassianSignInChoices.Problems(s.SignInMethod, s, nameof(BambooSettings.SignInMethod), nameof(BambooSettings.AccessToken), nameof(BambooSettings.Username), nameof(BambooSettings.BrowserPath)))
        {
            yield return problem;
        }

        if (s.BuildsKept is < 1 or > 10_000)
        {
            yield return new ValidationMessage("Builds kept must be between 1 and 10000.", nameof(BambooSettings.BuildsKept));
        }

        if (s.RefreshSeconds is < 0 or > 86_400)
        {
            yield return new ValidationMessage("Use Bamboo's answers for must be between 0 and 86400 seconds (a day).", nameof(BambooSettings.RefreshSeconds));
        }

        if (s.PlansAtATime is < 1 or > 64)
        {
            yield return new ValidationMessage("Plans at a time must be between 1 and 64.", nameof(BambooSettings.PlansAtATime));
        }

        if (s.RequestsPerPlan is < 1 or > 64)
        {
            yield return new ValidationMessage("Requests at a time per plan must be between 1 and 64.", nameof(BambooSettings.RequestsPerPlan));
        }
    }

    public Task<TestResult> TestAsync(object settings, IProgress<string> progress, CancellationToken ct) => TestAsync((BambooSettings)settings, progress, standIn: null, ct);

    /// <summary>Signs in the way the source will and asks Bamboo what the source would show: who is signed in, and what the address names.</summary>
    internal static async Task<TestResult> TestAsync(BambooSettings s, IProgress<string> progress, AtlassianStandIn? standIn, CancellationToken ct)
    {
        if (s.Url is null)
        {
            return new TestResult(false, "The address is not an http or https address.");
        }

        try
        {
            using var session = new AtlassianSession(AtlassianProduct.Bamboo, s, NullLogger.Instance, standIn);
            await session.SignInAsync(interactive: true, progress.Report, ct).ConfigureAwait(false);
            using var api = new AtlassianApi(session, s.Requests, NullLogger.Instance);
            var version = session.Version is null ? "" : $" (Bamboo {session.Version})";
            var who = $"Signed in to {session.Address.Base.Host}{version} as {session.User?.DisplayName} ({session.User?.Name}) {session.How}.";
            if (BambooScope.From(session.Address, out var problem) is not { } scope)
            {
                return new TestResult(false, $"{who} But {problem}");
            }

            progress.Report("Signed in; asking Bamboo what the address holds");
            switch (scope.Kind)
            {
                case BambooScopeKind.Server:
                    using (var projects = await api.GetAsync("project?expand=projects&max-result=1000&max-results=1000", ct).ConfigureAwait(false))
                    {
                        var list = projects.RootElement.Obj("projects");
                        var count = list.Has("size") ? list.Long("size") : list.Arr("project").Count();
                        return new TestResult(true, $"{who} The source shows the {count:N0} projects you can see.");
                    }

                case BambooScopeKind.Project:
                    using (var project = await api.FindAsync(BambooScope.Path("project", scope.ProjectKey), ct).ConfigureAwait(false))
                    {
                        return project is null
                            ? new TestResult(false, $"{who} But there is no project {scope.ProjectKey} that this account can see.")
                            : new TestResult(true, $"{who} The source shows the project {project.RootElement.Str("name")} ({scope.ProjectKey}).");
                    }

                default:
                    using (var plan = await api.FindAsync(BambooScope.Path("plan", scope.PlanKey), ct).ConfigureAwait(false))
                    {
                        return plan is null
                            ? new TestResult(false, $"{who} But there is no plan {scope.PlanKey} that this account can see.")
                            : new TestResult(true, $"{who} The source shows the plan {plan.RootElement.Str("shortName") ?? plan.RootElement.Str("name")} ({scope.PlanKey}) of {plan.RootElement.Str("projectName") ?? scope.ProjectKey}.");
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
