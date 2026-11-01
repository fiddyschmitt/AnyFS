using System.Diagnostics.CodeAnalysis;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Atlassian.Confluence;

/// <summary>
/// Confluence (type <c>confluence</c>, folder <c>confluence</c>, spec 7.10): a Confluence Data Center server, a space or a
/// page, read-only; every page a folder holding its Markdown, its attachments and its child pages.
/// </summary>
public sealed class ConfluencePlugin : ISourcePlugin, IValidatable, ITestable, ISettingVisibility
{
    public string TypeId => "confluence";

    public string DisplayName => "Confluence";

    public string Description => "A Confluence server (Data Center), space or page: every page a folder with its Markdown, its attachments and its child pages; each space's blog. Read-only.";

    public string TypeFolder => "confluence";

    /// <summary>A page with a folded corner on a rounded square of Confluence's blue, a PNG (Assets/confluence.png); not Atlassian's logo.</summary>
    public Stream? OpenIcon() => typeof(ConfluencePlugin).Assembly.GetManifestResourceStream("confluence.png");

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(ConfluenceSettings);

    public object CreateDefaultSettings() => new ConfluenceSettings();

    public ISource CreateSource(object settings) => new ConfluenceSource((ConfluenceSettings)settings);

    /// <summary>The server's host for a whole server; the space's key or the page's title and the host otherwise: <c>ENG (confluence.example.com)</c>.</summary>
    public string? SuggestName(object settings)
    {
        if (((ConfluenceSettings)settings).Url is not { } url)
        {
            return null;
        }

        var scope = AtlassianAddress.Candidates(url).Select(a => ConfluenceScope.From(a, out _)).FirstOrDefault(s => s is not null && s.Kind != ConfluenceScopeKind.Server);
        var name = scope?.Kind switch
        {
            ConfluenceScopeKind.Space => $"{scope.Space} ({url.Host})",
            ConfluenceScopeKind.Page when scope.Title is { Length: > 0 } title => $"{AtlassianNames.Title(title)} ({url.Host})",
            ConfluenceScopeKind.Page => $"page {scope.PageId} ({url.Host})",
            _ => url.Host,
        };
        var safe = AtlassianNames.Safe(name);
        return safe.Length == 0 ? null : safe;
    }

    /// <summary>The address is stored tidied: no fragment, no slash at the end of its path.</summary>
    public void NormaliseSettings(object settings)
    {
        var s = (ConfluenceSettings)settings;
        s.Address = s.Url?.AbsoluteUri ?? (s.Address ?? "").Trim();
    }

    public bool IsVisible(object settings, string propertyName)
    {
        var s = (ConfluenceSettings)settings;
        return propertyName switch
        {
            nameof(ConfluenceSettings.AccessToken) => s.EffectiveSignIn == AtlassianSignIn.Token,
            nameof(ConfluenceSettings.Username) or nameof(ConfluenceSettings.Password) => s.EffectiveSignIn == AtlassianSignIn.Password,
            nameof(ConfluenceSettings.BrowserPath) => s.EffectiveSignIn == AtlassianSignIn.WebSession,
            _ => true,
        };
    }

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (ConfluenceSettings)settings;
        if (s.Url is null)
        {
            yield return new ValidationMessage("The address is an http or https address, like https://confluence.example.com or https://confluence.example.com/display/KEY.", nameof(ConfluenceSettings.Address));
        }

        foreach (var problem in AtlassianSignInChoices.Problems(s.SignInMethod, s, nameof(ConfluenceSettings.SignInMethod), nameof(ConfluenceSettings.AccessToken), nameof(ConfluenceSettings.Username), nameof(ConfluenceSettings.BrowserPath)))
        {
            yield return problem;
        }

        if (s.RefreshSeconds is < 0 or > 86_400)
        {
            yield return new ValidationMessage("Check for changes every must be between 0 and 86400 seconds (a day).", nameof(ConfluenceSettings.RefreshSeconds));
        }

        if (s.Concurrency is < 1 or > 64)
        {
            yield return new ValidationMessage("Requests at a time must be between 1 and 64.", nameof(ConfluenceSettings.Concurrency));
        }
    }

    public Task<TestResult> TestAsync(object settings, IProgress<string> progress, CancellationToken ct) => TestAsync((ConfluenceSettings)settings, progress, standIn: null, ct);

    /// <summary>Signs in the way the source will and asks Confluence what the source would show: who is signed in, and what the address names.</summary>
    internal static async Task<TestResult> TestAsync(ConfluenceSettings s, IProgress<string> progress, AtlassianStandIn? standIn, CancellationToken ct)
    {
        if (s.Url is null)
        {
            return new TestResult(false, "The address is not an http or https address.");
        }

        try
        {
            using var session = new AtlassianSession(AtlassianProduct.Confluence, s, NullLogger.Instance, standIn);
            await session.SignInAsync(interactive: true, progress.Report, ct).ConfigureAwait(false);
            using var api = new AtlassianApi(session, s.Concurrency, NullLogger.Instance);
            var version = session.Version is null ? "" : $" (Confluence {session.Version})";
            var who = $"Signed in to {session.Address.Base.Host}{version} as {session.User?.DisplayName} ({session.User?.Name}) {session.How}.";
            if (ConfluenceScope.From(session.Address, out var problem) is not { } scope)
            {
                return new TestResult(false, $"{who} But {problem}");
            }

            progress.Report("Signed in; asking Confluence what the address holds");
            switch (scope.Kind)
            {
                case ConfluenceScopeKind.Space:
                    using (var space = await api.FindAsync($"space/{Uri.EscapeDataString(scope.Space)}", ct).ConfigureAwait(false))
                    {
                        return space is null
                            ? new TestResult(false, $"{who} But there is no space {scope.Space} that this account can read.")
                            : new TestResult(true, $"{who} The source shows the space {space.RootElement.Str("name")} ({scope.Space}).");
                    }

                case ConfluenceScopeKind.Page:
                    var relative = scope.PageId is { } id
                        ? $"content/{id}"
                        : $"content?spaceKey={Uri.EscapeDataString(scope.Space)}&title={Uri.EscapeDataString(scope.Title ?? "")}&type={(scope.PostingDay is null ? "page" : "blogpost")}";
                    using (var page = await api.FindAsync(relative, ct).ConfigureAwait(false))
                    {
                        var found = page is null ? default : page.RootElement.Has("results") ? page.RootElement.Arr("results").FirstOrDefault() : page.RootElement;
                        return found.ValueKind != System.Text.Json.JsonValueKind.Object
                            ? new TestResult(false, $"{who} But there is no such page that this account can read.")
                            : new TestResult(true, $"{who} The source shows the page {found.Str("title")} and the pages below it.");
                    }

                default:
                    using (var spaces = await api.GetAsync("space?type=global&limit=1", ct).ConfigureAwait(false))
                    {
                        return new TestResult(true, $"{who} The source shows the spaces you can read.");
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
