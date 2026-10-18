using System.Diagnostics.CodeAnalysis;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.SharePoint;

public sealed class SharePointEmbeddedSettings
{
    [Setting("Container type", Required = true, Order = 0, Placeholder = "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
        Hint = "The id of the container type whose containers to serve; the application that owns them, or your SharePoint administrator, knows it. Each container you are a member of becomes a top-level folder.")]
    public string ContainerTypeId { get; set; } = "";

    [Setting("Container", Order = 1, Placeholder = "Project files",
        Hint = "Leave empty to show every container you are a member of as a top-level folder, or name one to serve just that: its name as the application shows it, or its id, which also works for an account Graph lists no containers for.")]
    public string? Container { get; set; }

    [Setting("Account", Order = 2, Group = "Sign-in", Placeholder = "name@contoso.com",
        Hint = "The work or school account to sign in with. Optional: it pre-fills the sign-in page and picks the saved sign-in.")]
    public string? Account { get; set; }

    [Setting("Sign in with", Order = 3, Group = "Sign-in", Kind = SettingKind.Choice, Choices = [SharePointOnlineSettings.BrowserMethod, SharePointOnlineSettings.DeviceCodeMethod],
        Hint = "Browser: a sign-in page opens in your browser, the usual way. Device code: a code to enter on Microsoft's device sign-in page from any device (the message says where), for machines without a browser; some organisations block it.")]
    public string SignInMethod { get; set; } = SharePointOnlineSettings.BrowserMethod;

    [Setting("Cloud", Advanced = true, Order = 9, Group = "Sign-in", Kind = SettingKind.Choice,
        Choices = [MicrosoftCloud.AutomaticName, MicrosoftCloud.GlobalName, MicrosoftCloud.UsGovernmentName],
        Hint = "The Microsoft cloud the tenant is in. Automatic goes by the tenant's domain: one under .onmicrosoft.us is in the US Government cloud (GCC High), which needs your tenant in Tenant and your organisation's own app registration in Client ID.")]
    public string Cloud { get; set; } = MicrosoftCloud.AutomaticName;

    [Setting("Tenant", Advanced = true, Order = 10, Group = "Sign-in",
        Hint = "organizations (any work or school account), or your tenant's domain or id when the sign-in needs steering to it; in the US Government cloud, your tenant.")]
    public string Tenant { get; set; } = "organizations";

    [Setting("Client ID", Advanced = true, Order = 11, Group = "Sign-in",
        Hint = "The app registration AnyFS signs in as. The default is AnyFS's own, which works once your administrator has registered it on the container type; otherwise the owning application's client id, if that application allows a sign-in from a desktop app. In the US Government cloud, your organisation's own: AnyFS's exists only in the global cloud.")]
    public string ClientId { get; set; } = SharePointOnlinePlugin.DefaultClientId;

    [Setting("Connections", Advanced = true, Order = 20, Hint = "Parallel downloads per open file.")]
    public int Connections { get; set; } = 4;

    [Setting("Spool files below (MiB)", Advanced = true, Order = 21,
        Hint = "Files this small are downloaded whole when opened and kept in the cache; larger ones are read in ranges as the client asks.")]
    public int SpoolBelowMiB { get; set; } = 8;

    internal SignInMethod Method => string.Equals(SignInMethod, SharePointOnlineSettings.DeviceCodeMethod, StringComparison.OrdinalIgnoreCase) ? SharePoint.SignInMethod.DeviceCode : SharePoint.SignInMethod.Browser;

    internal string? PinnedContainer => string.IsNullOrWhiteSpace(Container) ? null : Container.Trim();

    /// <summary>The pinned container when it was given by id rather than by name.</summary>
    internal string? PinnedContainerId => PinnedContainer is { } pinned && GraphDocumentStore.LooksLikeContainerId(pinned) ? pinned : null;

    internal string? LoginHint => string.IsNullOrWhiteSpace(Account) ? null : Account.Trim();

    internal string EffectiveTenant => string.IsNullOrWhiteSpace(Tenant) ? "organizations" : Tenant.Trim();

    internal string EffectiveClientId => string.IsNullOrWhiteSpace(ClientId) ? SharePointOnlinePlugin.DefaultClientId : ClientId.Trim();

    /// <summary>The cloud to sign in to: the one chosen, else the tenant's (contoso.onmicrosoft.us is US Government), else the global cloud.</summary>
    internal MicrosoftCloud EffectiveCloud => MicrosoftCloud.Resolve(Cloud, null, Tenant);

    internal bool TryGetContainerType(out Guid id) => Guid.TryParse(ContainerTypeId?.Trim(), out id);
}

/// <summary>
/// SharePoint Embedded (type <c>sharepoint-embedded</c>, folder <c>sharepoint</c>, spec 7.5): the containers
/// of a container type, through Microsoft Graph, for a user who is a member of them. A container is a drive
/// like a document library, so this is the SharePoint Online source with containers as the top-level
/// folders. The sign-in is the same (the app registration AnyFS ships, or the owning application's), with
/// the one Graph scope Embedded uses; what the app may do inside the containers is set per app by whoever
/// registered the container type in the tenant.
/// </summary>
public sealed class SharePointEmbeddedPlugin : ISourcePlugin, IValidatable, ITestable
{
    /// <summary>The delegated Graph permissions an organisation's own registration needs (<see cref="EntraSignIn.ContainerScopes"/>), as messages list them.</summary>
    internal const string RegistrationPermissions = "User.Read and FileStorageContainer.Selected";

    /// <summary>What an organisation's own registration needs besides its permissions.</summary>
    internal const string RegistrationExtra = ", and registers it on the container type";

    public string TypeId => "sharepoint-embedded";

    public string DisplayName => "SharePoint Embedded";

    public string Description => "The containers a SharePoint Embedded application keeps your files in, through Microsoft Graph: sign in with your work account; reads, writes, renames.";

    public string TypeFolder => "sharepoint";

    public Stream? OpenIcon() => null;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(SharePointEmbeddedSettings);

    public object CreateDefaultSettings() => new SharePointEmbeddedSettings();

    public ISource CreateSource(object settings) => new SharePointEmbeddedSource((SharePointEmbeddedSettings)settings);

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (SharePointEmbeddedSettings)settings;
        if (!s.TryGetContainerType(out _))
        {
            yield return new ValidationMessage("The container type is its id, a GUID like 91710488-5756-407f-9046-fbe5f0b4de73.", nameof(SharePointEmbeddedSettings.ContainerTypeId));
        }

        if (s.PinnedContainer is { } container && (container.Contains('/') || container.Contains('\\')))
        {
            yield return new ValidationMessage("The container is a name, as it shows in the application, not a path.", nameof(SharePointEmbeddedSettings.Container));
        }

        if (s.LoginHint is { } account && (!account.Contains('@') || account.Any(char.IsWhiteSpace)))
        {
            yield return new ValidationMessage("The account is a sign-in name like name@contoso.com.", nameof(SharePointEmbeddedSettings.Account));
        }

        if (s.EffectiveTenant.Any(char.IsWhiteSpace) || s.EffectiveTenant.Contains('/'))
        {
            yield return new ValidationMessage("The tenant is organizations, a domain like contoso.onmicrosoft.com, or a tenant id.", nameof(SharePointEmbeddedSettings.Tenant));
        }

        if (!Guid.TryParse(s.EffectiveClientId, out _))
        {
            yield return new ValidationMessage("The client ID is the app registration's Application (client) ID, a GUID.", nameof(SharePointEmbeddedSettings.ClientId));
        }

        foreach (var message in CloudSettings.Check(s.Cloud, s.EffectiveCloud, s.Tenant, s.EffectiveClientId, siteHost: null, RegistrationPermissions, tenantRequired: true, RegistrationExtra))
        {
            yield return message;
        }

        if (s.Connections is < 1 or > 16)
        {
            yield return new ValidationMessage("Connections must be between 1 and 16.", nameof(SharePointEmbeddedSettings.Connections));
        }

        if (s.SpoolBelowMiB is < 0 or > 1024)
        {
            yield return new ValidationMessage("Spool files below must be between 0 (never) and 1024 MiB.", nameof(SharePointEmbeddedSettings.SpoolBelowMiB));
        }
    }

    /// <summary>Signs in and lists the containers of the type the account is a member of; the sign-in is kept for the source started from the same dialog.</summary>
    public async Task<TestResult> TestAsync(object settings, IProgress<string> progress, CancellationToken ct)
    {
        var s = (SharePointEmbeddedSettings)settings;
        if (!s.TryGetContainerType(out var containerType))
        {
            return new TestResult(false, "The container type is its id, a GUID.");
        }

        var cloud = s.EffectiveCloud;
        if (CloudSettings.NeedsOwnRegistration(cloud, s.EffectiveClientId))
        {
            return new TestResult(false, CloudSettings.OwnRegistrationNeeded(cloud, RegistrationPermissions, RegistrationExtra));
        }

        var signIn = new EntraSignIn(new SignInOptions(
            s.EffectiveClientId, s.EffectiveTenant, s.LoginHint, s.Method, EntraSignIn.ContainerScopes,
            Progress: progress.Report,
            Prompt: null,
            LoadCache: () => EntraSignIn.RecentCache(s.EffectiveClientId, s.EffectiveTenant, s.LoginHint),
            SaveCache: _ => { },
            Cloud: cloud), NullLogger.Instance);
        try
        {
            progress.Report(cloud.IsGlobal ? "Signing in" : $"Signing in to the {cloud.Name} cloud");
            await signIn.SignInAsync(ct).ConfigureAwait(false);
            using var graph = new GraphClient((token, refresh) => signIn.GetAccessTokenAsync(token, refresh), NullLogger.Instance, cloud: cloud);
            if (s.PinnedContainerId is { } id)
            {
                progress.Report($"Signed in as {signIn.Username}; looking up container {id}");
                await GraphDocumentStore.GetContainerAsync(graph, containerType, id, ct).ConfigureAwait(false);
                return new TestResult(true, $"Signed in as {signIn.Username}. Container {id} is there and active.");
            }

            progress.Report($"Signed in as {signIn.Username}; listing the containers");
            var containers = await GraphDocumentStore.ListContainersAsync(graph, containerType, ct).ConfigureAwait(false);
            if (containers.Count == 0)
            {
                return new TestResult(false, $"Signed in as {signIn.Username}, but Graph lists no container of type {containerType} for this account. It lists only containers the account is a direct member of, and nothing at all for an account without a OneDrive; a container's id in the Container setting works regardless.");
            }

            var names = string.Join(", ", containers.Select(c => c.Name));
            if (s.PinnedContainer is { } pinned && !containers.Any(c => string.Equals(c.Name, pinned, StringComparison.OrdinalIgnoreCase)))
            {
                return new TestResult(false, $"Signed in as {signIn.Username}. No container named {pinned}; this account's containers of the type are: {names}.");
            }

            return new TestResult(true, $"Signed in as {signIn.Username}. {containers.Count} container{(containers.Count == 1 ? "" : "s")} of the type: {names}.");
        }
        catch (SignInRequiredException ex)
        {
            return new TestResult(false, ex.Message);
        }
        catch (VfsException ex) when (ex.Error == VfsError.AccessDenied)
        {
            return new TestResult(false, $"Signed in as {signIn.Username}, but Graph refused: {ex.Message}. The application (client ID {s.EffectiveClientId}) must be registered on the container type with permission to read and write its containers, and the account must be a member of the container.");
        }
        catch (VfsException ex) when (ex.Error == VfsError.NotFound && s.PinnedContainerId is { } id)
        {
            return new TestResult(false, $"Signed in as {signIn.Username}, but there is no container {id} that this account can see.");
        }
        catch (VfsException ex)
        {
            return new TestResult(false, ex.Message);
        }
    }
}

/// <summary>SharePoint Embedded: the shared <see cref="SharePointSource"/> over a <see cref="GraphDocumentStore"/> whose libraries are containers.</summary>
public sealed class SharePointEmbeddedSource : SharePointSource
{
    private readonly SharePointEmbeddedSettings _settings;

    public SharePointEmbeddedSource(SharePointEmbeddedSettings settings)
    {
        _settings = settings;
    }

    protected override string? PinnedLibrary => _settings.PinnedContainer;

    protected override int Connections => _settings.Connections;

    protected override long SpoolBelowBytes => (long)_settings.SpoolBelowMiB << 20;

    protected override string Target => $"container type {_settings.ContainerTypeId}";

    private protected override async Task<IDocumentStore> ConnectAsync(CancellationToken ct)
    {
        if (!_settings.TryGetContainerType(out var containerType))
        {
            throw new SourceFailedException("The container type is its id, a GUID.", permanent: true);
        }

        if (!Guid.TryParse(_settings.EffectiveClientId, out _))
        {
            throw new SourceFailedException("The client ID is not a GUID.", permanent: true);
        }

        // the settings dialog checks this too, but a source saved before (or edited by hand) gets here unchecked
        var cloud = _settings.EffectiveCloud;
        if (CloudSettings.NeedsOwnRegistration(cloud, _settings.EffectiveClientId))
        {
            throw new SourceFailedException(CloudSettings.OwnRegistrationNeeded(cloud, SharePointEmbeddedPlugin.RegistrationPermissions, SharePointEmbeddedPlugin.RegistrationExtra), permanent: true);
        }

        var secrets = Context.Secrets;
        var signIn = new EntraSignIn(new SignInOptions(
            _settings.EffectiveClientId, _settings.EffectiveTenant, _settings.LoginHint, _settings.Method, EntraSignIn.ContainerScopes,
            Progress: message => Context.ReportProgress(message),
            Prompt: (title, message) => Context.PromptAsync(new PromptRequest(PromptKind.YesNo, title, message), ct),
            LoadCache: () => secrets.Get("msal-cache") is { } saved ? Convert.FromBase64String(saved) : null,
            SaveCache: bytes => secrets.Set("msal-cache", Convert.ToBase64String(bytes)),
            Cloud: cloud), Logger);
        Context.ReportProgress(cloud.IsGlobal ? "Signing in to Microsoft 365" : $"Signing in to Microsoft 365 in the {cloud.Name} cloud");
        try
        {
            await signIn.SignInAsync(ct).ConfigureAwait(false);
        }
        catch (SignInRequiredException ex)
        {
            throw new SourceFailedException(ex.Message, permanent: ex.Message.Contains("Conditional Access", StringComparison.Ordinal), ex);
        }

        var graph = new GraphClient((token, refresh) => signIn.GetAccessTokenAsync(token, refresh), Logger, MarkDegraded, MarkHealthy, cloud: cloud);
        Context.ReportProgress($"Listing the containers of type {containerType}");
        try
        {
            var store = await GraphDocumentStore.ForContainersAsync(graph, containerType, _settings.PinnedContainerId, ct).ConfigureAwait(false);
            Context.ReportProgress($"Signed in as {signIn.Username}");
            return store;
        }
        catch (SignInRequiredException ex)
        {
            graph.Dispose();
            throw new SourceFailedException(ex.Message, permanent: false, ex);
        }
        catch (VfsException ex) when (ex.Error is VfsError.AccessDenied or VfsError.InvalidPath or VfsError.NotFound)
        {
            graph.Dispose();
            throw new SourceFailedException($"{signIn.Username} cannot reach the containers of type {containerType}: {ex.Message}. The application (client ID {_settings.EffectiveClientId}) must be registered on the container type, and the account must be a member of the container.", permanent: true, ex);
        }
        catch (VfsException ex)
        {
            graph.Dispose();
            throw new SourceFailedException($"container type {containerType}: {ex.Message}", permanent: false, ex);
        }
    }
}
