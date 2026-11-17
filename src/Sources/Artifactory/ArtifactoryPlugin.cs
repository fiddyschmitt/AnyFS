using System.Diagnostics.CodeAnalysis;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Artifactory;

/// <summary>
/// Artifactory (type <c>artifactory</c>, folder <c>artifactory</c>, spec 7.13): a JFrog Artifactory server, a repository on
/// it or a folder in a repository, read-only; each repository's folders and files as Artifactory holds them. A server of
/// one's own and a hosted one (<c>name.jfrog.io</c>) alike: they share one API.
/// </summary>
public sealed class ArtifactoryPlugin : ISourcePlugin, IValidatable, ITestable, ISettingVisibility
{
    public string TypeId => "artifactory";

    public string DisplayName => "Artifactory";

    public string Description => "A JFrog Artifactory server, a repository on it or a folder in one (your own server or a hosted one): the folders and files each repository holds. Read-only.";

    public string TypeFolder => "artifactory";

    /// <summary>Artifactory's mark, a PNG (Assets/artifactory.png).</summary>
    public Stream? OpenIcon() => typeof(ArtifactoryPlugin).Assembly.GetManifestResourceStream("artifactory.png");

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(ArtifactorySettings);

    public object CreateDefaultSettings() => new ArtifactorySettings();

    public ISource CreateSource(object settings) => new ArtifactorySource((ArtifactorySettings)settings);

    /// <summary>The server's host for a whole server; the repository's key or the folder's name, and the host otherwise: <c>libs-release-local (artifactory.example.com)</c>.</summary>
    public string? SuggestName(object settings)
    {
        if (((ArtifactorySettings)settings).Url is not { } url)
        {
            return null;
        }

        // where Artifactory is on the host is not known before it is asked: the likeliest reading of the address names the scope
        var address = ArtifactoryAddress.Candidates(url)[0];
        var name = address.Scope switch
        {
            ArtifactoryScope.Folder => $"{address.Path[(address.Path.LastIndexOf('/') + 1)..]} ({url.Host})",
            ArtifactoryScope.Repository => $"{address.Repository} ({url.Host})",
            _ => url.Host,
        };
        var safe = SafeNames.Safe(name, trim: true);
        return safe.Length == 0 ? null : safe;
    }

    /// <summary>The address is stored tidied: no query, no slash at the end, the older pages' address as the repository's own.</summary>
    public void NormaliseSettings(object settings)
    {
        var s = (ArtifactorySettings)settings;
        s.Address = s.Url?.AbsoluteUri.TrimEnd('/') ?? (s.Address ?? "").Trim();
    }

    public bool IsVisible(object settings, string propertyName)
    {
        var s = (ArtifactorySettings)settings;
        return propertyName switch
        {
            nameof(ArtifactorySettings.AccessToken) => s.EffectiveSignIn == ArtifactorySignIn.Token,
            nameof(ArtifactorySettings.Username) or nameof(ArtifactorySettings.Password) => s.EffectiveSignIn == ArtifactorySignIn.Password,
            nameof(ArtifactorySettings.BrowserPath) => s.EffectiveSignIn == ArtifactorySignIn.WebSession,

            // which kinds of repository a server shows: nothing to choose for an address that names one
            nameof(ArtifactorySettings.LocalRepositories) or nameof(ArtifactorySettings.RemoteRepositories) or nameof(ArtifactorySettings.VirtualRepositories)
                => s.Url is not { } url || ArtifactoryAddress.Candidates(url)[0].Scope == ArtifactoryScope.Server,
            _ => true,
        };
    }

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (ArtifactorySettings)settings;
        if (s.Url is null)
        {
            yield return new ValidationMessage("The address is an http or https address, like https://artifactory.example.com/artifactory or https://example.jfrog.io/artifactory/libs-release-local.", nameof(ArtifactorySettings.Address));
        }

        if (ArtifactorySettings.SignInFrom(s.SignInMethod) is null)
        {
            yield return new ValidationMessage($"Sign in with one of: {ArtifactorySettings.TokenChoice}, {ArtifactorySettings.WebSessionChoice}, {ArtifactorySettings.PasswordChoice}, {ArtifactorySettings.AnonymousChoice}.", nameof(ArtifactorySettings.SignInMethod));
        }

        if (s.EffectiveSignIn == ArtifactorySignIn.Token && s.Token is null)
        {
            yield return new ValidationMessage("Give an access token: an identity token, an access token or an API key.", nameof(ArtifactorySettings.AccessToken));
        }

        if (s.EffectiveSignIn == ArtifactorySignIn.Password && s.User is null)
        {
            yield return new ValidationMessage("Give the user name to sign in with.", nameof(ArtifactorySettings.Username));
        }

        if (s.EffectiveSignIn == ArtifactorySignIn.WebSession && s.Browser is { } browser && !File.Exists(browser))
        {
            yield return new ValidationMessage("No browser at that path; leave it empty to find Edge or Chrome automatically.", nameof(ArtifactorySettings.BrowserPath), IsError: false);
        }

        if (s.Url is { } server && ArtifactoryAddress.Candidates(server)[0].Scope == ArtifactoryScope.Server && !s.LocalRepositories && !s.RemoteRepositories && !s.VirtualRepositories)
        {
            yield return new ValidationMessage("With no kind of repository chosen the source shows nothing: turn on local, remote or virtual repositories.", nameof(ArtifactorySettings.LocalRepositories));
        }

        if (s.RefreshSeconds is < 0 or > 86_400)
        {
            yield return new ValidationMessage("Use Artifactory's answers for must be between 0 and 86400 seconds (a day).", nameof(ArtifactorySettings.RefreshSeconds));
        }

        if (s.RepositoriesAtATime is < 1 or > 64)
        {
            yield return new ValidationMessage("Repositories at a time must be between 1 and 64.", nameof(ArtifactorySettings.RepositoriesAtATime));
        }

        if (s.RequestsPerRepository is < 1 or > 64)
        {
            yield return new ValidationMessage("Requests at a time per repository must be between 1 and 64.", nameof(ArtifactorySettings.RequestsPerRepository));
        }
    }

    public Task<TestResult> TestAsync(object settings, IProgress<string> progress, CancellationToken ct) => TestAsync((ArtifactorySettings)settings, progress, standIn: null, ct);

    /// <summary>Signs in the way the source will and asks Artifactory what the source would show: who is signed in, and what the address names.</summary>
    internal static async Task<TestResult> TestAsync(ArtifactorySettings s, IProgress<string> progress, ArtifactoryStandIn? standIn, CancellationToken ct)
    {
        if (s.Url is null)
        {
            return new TestResult(false, "The address is not an http or https address.");
        }

        try
        {
            using var session = new ArtifactorySession(s, NullLogger.Instance, standIn);
            await session.SignInAsync(interactive: true, progress.Report, ct).ConfigureAwait(false);
            using var api = new ArtifactoryApi(session, s.Requests, NullLogger.Instance);
            var version = session.Version is null ? "" : $" (Artifactory {session.Version})";
            var who = session.Method == ArtifactorySignIn.Anonymous
                ? $"Reached {session.Address.Base.Host}{version} {session.How}."
                : $"Signed in to {session.Address.Base.Host}{version}{(session.User is null ? "" : $" as {session.User}")} {session.How}.";
            progress.Report("Signed in; asking Artifactory what the address holds");
            // the sign-in's check has just read them, but for a web session's
            var repositories = session.TakeRepositories(TimeSpan.FromMinutes(1));
            if (repositories is null)
            {
                using var doc = await api.GetAsync("repositories", ct).ConfigureAwait(false);
                repositories = ArtRepository.ListOf(doc.RootElement);
            }

            var address = session.Address;
            if (address.Scope == ArtifactoryScope.Server)
            {
                var shown = repositories.Count(r => r.IsRemote ? s.RemoteRepositories : r.IsVirtual ? s.VirtualRepositories : s.LocalRepositories);
                var kinds = string.Join(", ", new[] { s.LocalRepositories ? "local" : null, s.RemoteRepositories ? "remote" : null, s.VirtualRepositories ? "virtual" : null }.OfType<string>());
                return repositories.Count == 0
                    ? new TestResult(false, $"{who} But it shows this account no repositories.")
                    : new TestResult(true, $"{who} The source shows {shown:N0} of the {repositories.Count:N0} repositories you can see ({kinds}).");
            }

            var repository = repositories.FirstOrDefault(r => r.Key.Equals(address.Repository, StringComparison.OrdinalIgnoreCase))
                ?? (address.Repository.EndsWith(ArtRepository.CacheSuffix, StringComparison.OrdinalIgnoreCase)
                    ? repositories.FirstOrDefault(r => r.IsRemote && r.Key.Equals(address.Repository[..^ArtRepository.CacheSuffix.Length], StringComparison.OrdinalIgnoreCase))
                    : null);
            if (repository is null)
            {
                return new TestResult(false, $"{who} But there is no repository {address.Repository} that this account can see.");
            }

            var kind = $"{repository.Type.ToLowerInvariant()}{(repository.PackageType.Length == 0 ? "" : ", " + repository.PackageType)}";
            if (address.Path.Length == 0)
            {
                return new TestResult(true, $"{who} The source shows the repository {repository.Key} ({kind}).");
            }

            using var item = await api.FindAsync($"storage/{ArtifactoryAddress.Escaped(repository.StorageKey, address.Path)}", ct).ConfigureAwait(false);
            if (item is null)
            {
                return new TestResult(false, $"{who} But the repository {repository.Key} has no {address.Path} that this account can see.");
            }

            var folder = item.RootElement.TryGetProperty("children", out _) ? address.Path : address.Path.Contains('/') ? address.Path[..address.Path.LastIndexOf('/')] : "";
            return new TestResult(true, folder.Length == 0
                ? $"{who} The address names a file at the top of {repository.Key}: the source shows the repository ({kind})."
                : $"{who} The source shows the folder {folder} of the repository {repository.Key} ({kind}).");
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
