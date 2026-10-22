using System.Diagnostics.CodeAnalysis;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.ThreeDX;

/// <summary>
/// 3DX (type <c>3dx</c>, folder <c>3dx</c>, spec 7.6): a 3DEXPERIENCE platform's documents, read-only, a folder per
/// document with its files, as Mount3DX serves them. Signs in with the web session (single sign-on first, else a browser),
/// or with a Windows account, as yourself or with a user name and password.
/// </summary>
public sealed class ThreeDXPlugin : ISourcePlugin, IValidatable, ITestable, ISettingVisibility
{
    public string TypeId => "3dx";

    public string DisplayName => "3DX";

    public string Description => "A 3DEXPERIENCE platform's documents (3DSpace), a folder per document revision with its files, found through 3DSearch and kept for quick listings, as Mount3DX shows them. Read-only.";

    public string TypeFolder => "3dx";

    public Stream? OpenIcon() => null;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(ThreeDXSettings);

    public object CreateDefaultSettings() => new ThreeDXSettings();

    public ISource CreateSource(object settings) => new ThreeDXSource((ThreeDXSettings)settings);

    /// <summary>The server's name as the source's: its host, without a port; null until the address parses.</summary>
    public string? SuggestName(object settings)
    {
        var host = ((ThreeDXSettings)settings).Space?.Host;
        if (string.IsNullOrEmpty(host))
        {
            return null;
        }

        var safe = string.Concat(host.Select(c => c < ' ' || "\\/:*?\"<>|".Contains(c) ? '-' : c)).Trim().Trim('.').Trim();
        return safe.Length == 0 ? null : safe;
    }

    /// <summary>The 3DSpace URL is stored as the service's address: trimmed, a page pasted after it cut off, no slash at the end.</summary>
    public void NormaliseSettings(object settings)
    {
        var s = (ThreeDXSettings)settings;
        if (Uri.TryCreate((s.ServerUrl ?? "").Trim(), UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
        {
            s.ServerUrl = ThreeDXSettings.ServiceAddress(url);
        }
        else
        {
            s.ServerUrl = (s.ServerUrl ?? "").Trim();
        }
    }

    public bool IsVisible(object settings, string propertyName)
    {
        var s = (ThreeDXSettings)settings;
        return propertyName switch
        {
            nameof(ThreeDXSettings.Username) or nameof(ThreeDXSettings.Password) => s.EffectiveSignIn == ThreeDXSignIn.Windows,
            nameof(ThreeDXSettings.BrowserPath) => s.EffectiveSignIn == ThreeDXSignIn.WebSession,
            _ => true,
        };
    }

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (ThreeDXSettings)settings;
        if (s.Space is not { } space)
        {
            yield return new ValidationMessage("The 3DSpace URL is an http or https address, like https://3dx.contoso.com/3dspace.", nameof(ThreeDXSettings.ServerUrl));
        }
        else if (space.AbsolutePath.Trim('/').Length == 0)
        {
            yield return new ValidationMessage("3DSpace is usually at a path of its own on the server, like https://3dx.contoso.com/3dspace.", nameof(ThreeDXSettings.ServerUrl), IsError: false);
        }

        if (ThreeDXSettings.SignInFrom(s.SignInMethod) is null)
        {
            yield return new ValidationMessage($"Sign in with one of: {ThreeDXSettings.WebSession}, {ThreeDXSettings.WindowsCurrent}, {ThreeDXSettings.Windows}.", nameof(ThreeDXSettings.SignInMethod));
        }

        if (s.EffectiveSignIn is ThreeDXSignIn.Windows or ThreeDXSignIn.WindowsCurrent)
        {
            foreach (var problem in WindowsAccount.Problems(s.IsWindowsCurrent, s.EffectiveSignIn == ThreeDXSignIn.Windows ? s.Username : null, s.EffectiveSignIn == ThreeDXSignIn.Windows ? s.Password : null, nameof(ThreeDXSettings.SignInMethod), nameof(ThreeDXSettings.Username)))
            {
                yield return problem;
            }
        }

        if (s.EffectiveSignIn == ThreeDXSignIn.WebSession && s.Browser is { } browser && !File.Exists(browser))
        {
            yield return new ValidationMessage("No browser at that path; leave it empty to find Edge or Chrome automatically.", nameof(ThreeDXSettings.BrowserPath), IsError: false);
        }

        if (s.RefreshMinutes is < 0 or > 10080)
        {
            yield return new ValidationMessage("Refresh every must be between 0 (at the start only) and 10080 minutes (a week).", nameof(ThreeDXSettings.RefreshMinutes));
        }

        if (s.KeepAliveMinutes is < 0 or > 1440)
        {
            yield return new ValidationMessage("Keep the session alive every must be between 0 (never) and 1440 minutes.", nameof(ThreeDXSettings.KeepAliveMinutes));
        }

        if (s.QueryThreads is < 1 or > 32)
        {
            yield return new ValidationMessage("Queries at a time must be between 1 and 32.", nameof(ThreeDXSettings.QueryThreads));
        }

        if (s.Connections is < 1 or > 16)
        {
            yield return new ValidationMessage("Connections must be between 1 and 16.", nameof(ThreeDXSettings.Connections));
        }

        if (s.SpoolBelowMiB is < 0 or > 4096)
        {
            yield return new ValidationMessage("Spool files below must be between 0 (never) and 4096 MiB.", nameof(ThreeDXSettings.SpoolBelowMiB));
        }

        if (string.IsNullOrWhiteSpace(s.Tenant) || s.Tenant.Trim().Any(char.IsWhiteSpace))
        {
            yield return new ValidationMessage("The tenant is one word: OnPremise for a platform of your own.", nameof(ThreeDXSettings.Tenant));
        }
    }

    /// <summary>
    /// Signs in the way the source will (a browser window for the web session when single sign-on does not get through)
    /// and asks 3DX what the source would show: who is signed in, how many documents the search finds, the first few. The
    /// session is handed to the source started from the same dialog, so it does not sign in again.
    /// </summary>
    public async Task<TestResult> TestAsync(object settings, IProgress<string> progress, CancellationToken ct)
    {
        var s = (ThreeDXSettings)settings;
        if (s.Space is not { } space)
        {
            return new TestResult(false, "The 3DSpace URL is not an http or https address.");
        }

        if (s.EffectiveSignIn == ThreeDXSignIn.WindowsCurrent && !OperatingSystem.IsWindows())
        {
            return new TestResult(false, "Signing in as yourself works on Windows only: choose Windows account and give a user name and password, or Web session.");
        }

        try
        {
            using var session = new ThreeDXSession(s, NullLogger.Instance, secrets: null, new SessionOptions(UseKept: false));
            await session.SignInAsync(interactive: true, progress.Report, ct).ConfigureAwait(false);
            var client = new ThreeDXClient(session, ThreeDXDates.Resolve(s.DateOrder), NullLogger.Instance);
            progress.Report($"Signed in to {space.Host}; asking who you are and how many documents there are");
            var (name, context) = await client.PersonAsync(ct).ConfigureAwait(false);
            var search = await client.SearchAsync(ct).ConfigureAwait(false);
            var (count, _) = await client.CountAsync(s.Query, s.EffectiveTenant, ct).ConfigureAwait(false);
            var first = count == 0 ? [] : await client.DocumentsAsync(await client.PageAsync(0, 5, null, s.Query, s.EffectiveTenant, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            session.Save();

            var who = name is null ? "" : $" as {name}";
            var examples = first.Count == 0 ? "" : $"; the first: {string.Join(", ", first.Select(d => ThreeDXNames.DocumentFolder(d.Name, d.Revision, d.Title)))}";
            return new TestResult(count > 0, $"Signed in to {space.Host}{who} {session.How} (security context {context}). 3DSearch ({search.Host}) finds {count:N0} document{(count == 1 ? "" : "s")}{examples}."
                + (count == 0 ? " The source would show nothing: check the search under Advanced, and that this account sees documents in 3DX." : ""));
        }
        catch (SignInRequiredException ex)
        {
            return new TestResult(false, ex.Message);
        }
        catch (VfsException ex)
        {
            return new TestResult(false, ex.Message);
        }
        catch (SourceFailedException ex)
        {
            return new TestResult(false, ex.Message);
        }
    }
}
