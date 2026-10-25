using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using MsalLogLevel = Microsoft.Identity.Client.LogLevel;

namespace AnyFS.Sources.Microsoft365;

/// <summary>How the user signs in when there is no token to reuse.</summary>
public enum SignInMethod
{
    /// <summary>A sign-in page in the system browser, with the reply caught on a loopback port. Falls back to a device code when no browser can be opened.</summary>
    Browser,
    /// <summary>A code to type at microsoft.com/devicelogin on any device. Organisations may block it (spike 9: the sandbox did).</summary>
    DeviceCode,
}

/// <summary>What a sign-in needs from its caller: who it is for, the app and tenant, how to talk to the user, and where the token cache lives.</summary>
/// <param name="Service">The service signed in to, as the device-code prompt and the log lines name it.</param>
/// <param name="Scopes">The delegated Microsoft Graph scopes to ask for: SharePoint's for a site or for Embedded's containers, Outlook's for a mailbox.</param>
/// <param name="Progress">One-line status while signing in ("Waiting for the sign-in in your browser").</param>
/// <param name="Prompt">Puts a message in front of the user without waiting for an answer (the device code), or null when nobody can be asked.</param>
/// <param name="LoadCache">The serialised MSAL cache from the last run, or null.</param>
/// <param name="SaveCache">Stores the serialised MSAL cache after it changed.</param>
/// <param name="OpenBrowser">Shows the sign-in page: false when it could not (the device code is used instead). Null: the system browser.</param>
/// <param name="ForceLogin">The browser sign-in asks for credentials rather than offering the browser's signed-in accounts (for signing in as exactly the named account).</param>
/// <param name="Cloud">The Microsoft cloud to sign in to and whose Graph the scopes are for; null: the global cloud.</param>
public sealed record SignInOptions(
    Microsoft365Service Service,
    string ClientId,
    string Tenant,
    string? LoginHint,
    SignInMethod Method,
    IReadOnlyList<string> Scopes,
    Action<string> Progress,
    Func<string, string, Task>? Prompt,
    Func<byte[]?> LoadCache,
    Action<byte[]> SaveCache,
    Func<Uri, bool>? OpenBrowser = null,
    bool ForceLogin = false,
    MicrosoftCloud? Cloud = null);

/// <summary>
/// The Microsoft identity platform sign-in behind the Microsoft 365 sources' app sign-ins (SharePoint Online and
/// Embedded, Exchange Online), on MSAL. Silent first (the cache from the last run, refresh tokens included); otherwise
/// the system browser or a device code, as the settings say. The cache is handed back to the caller serialised, so the
/// source keeps it in its secret store; a process-wide copy also bridges the settings dialog's Test button and the
/// source it then starts, so the user signs in once.
/// </summary>
public sealed class EntraSignIn
{
    /// <summary>The AnyFS app registration (provisioning/sharepoint/README.md): a multi-tenant public client with delegated Graph scopes, in the global cloud only.</summary>
    public const string DefaultClientId = "824dc5b9-f3c4-42b7-871d-6f0de8bc0a4a";

    /// <summary>The settings choice of the app sign-in in the browser, as every Microsoft 365 source names it.</summary>
    public const string WithBrowser = "App sign-in, browser";

    /// <summary>The settings choice of the app sign-in with a device code.</summary>
    public const string WithDeviceCode = "App sign-in, device code";

    private static readonly TimeSpan InteractiveTimeout = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, byte[]> RecentCaches = new(StringComparer.Ordinal);

    private readonly SignInOptions _options;
    private readonly ILogger _logger;
    private readonly IPublicClientApplication _app;
    private readonly MicrosoftCloud _cloud;
    private readonly IReadOnlyList<string> _scopes;
    private byte[]? _cache;
    private bool _cacheLoaded;
    private IAccount? _account;

    public EntraSignIn(SignInOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
        _cloud = options.Cloud ?? MicrosoftCloud.Global;
        _scopes = _cloud.QualifyScopes(options.Scopes);
        var builder = PublicClientApplicationBuilder.Create(options.ClientId)
            .WithAuthority(_cloud.Instance, options.Tenant)
            .WithDefaultRedirectUri()
            .WithClientName("AnyFS");
        if (logger.IsEnabled(LogLevel.Debug))
        {
            // MSAL's own trace (no personal data), for seeing when it goes to the token endpoint and why
            builder = builder.WithLogging((level, message, _) => logger.Log(level switch
            {
                MsalLogLevel.Error => LogLevel.Warning,
                MsalLogLevel.Warning => LogLevel.Information,
                _ => LogLevel.Debug,
            }, "msal: {Message}", message), MsalLogLevel.Verbose, enablePiiLogging: false, enableDefaultPlatformLogging: false);
        }

        _app = builder.Build();
        _app.UserTokenCache.SetBeforeAccess(OnBeforeCacheAccess);
        _app.UserTokenCache.SetAfterAccess(OnAfterCacheAccess);
    }

    /// <summary>The signed-in account's name, once <see cref="SignInAsync"/> has succeeded.</summary>
    public string? Username => _account?.Username;

    private string Prefix => _options.Service.LogPrefix;

    /// <summary>The scopes asked for, qualified for the cloud (for tests).</summary>
    internal IReadOnlyList<string> Scopes => _scopes;

    /// <summary>The key under which a sign-in's cache is remembered in this process (the Test button hands over to the source through it).</summary>
    public static string RecentCacheKey(string clientId, string tenant, string? loginHint) => $"{clientId}|{tenant}|{loginHint}".ToLowerInvariant();

    public static byte[]? RecentCache(string clientId, string tenant, string? loginHint) => RecentCaches.TryGetValue(RecentCacheKey(clientId, tenant, loginHint), out var bytes) ? bytes : null;

    /// <summary>Puts a cache where the next sign-in in this process finds it first (the tests seed the Test button's sign-in this way).</summary>
    internal static void Remember(string clientId, string tenant, string? loginHint, byte[] cache) => RecentCaches[RecentCacheKey(clientId, tenant, loginHint)] = cache;

    /// <summary>Signs in: silently from the cache when it can, else as the settings say. Throws <see cref="SignInRequiredException"/> for a policy or user refusal.</summary>
    public async Task SignInAsync(CancellationToken ct)
    {
        var accounts = await _app.GetAccountsAsync().ConfigureAwait(false);
        // with an account named, only that account's saved sign-in will do: falling back to another one signed the
        // tests in as the wrong person without a word (18-Oct-2026)
        var preferred = _options.LoginHint is { } hint
            ? accounts.FirstOrDefault(a => string.Equals(a.Username, hint, StringComparison.OrdinalIgnoreCase))
            : accounts.FirstOrDefault();
        if (preferred is not null)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var silent = await Silent(preferred, forceRefresh: false).ExecuteAsync(ct).ConfigureAwait(false);
                    _account = silent.Account;
                    _logger.LogDebug("{Prefix}: signed in silently as {Account} (token from {Source})", Prefix, silent.Account.Username, silent.AuthenticationResultMetadata.TokenSource);
                    return;
                }
                catch (MsalThrottledUiRequiredException ex) when (attempt <= 4)
                {
                    // MSAL replays a recent refusal for a while rather than ask the server again; that is not a
                    // reason to make the user sign in, so wait it out before deciding
                    _logger.LogInformation("{Prefix}: the sign-in for {Account} is throttled ({Code}); waiting 30 s before trying again", Prefix, preferred.Username, ex.ErrorCode);
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                }
                catch (MsalUiRequiredException ex)
                {
                    _logger.LogInformation("{Prefix}: the saved sign-in for {Account} needs renewing ({Code}: {Message})", Prefix, preferred.Username, ex.ErrorCode, ex.Message.Split('\n')[0].Trim());
                    break;
                }
            }
        }

        AuthenticationResult result;
        if (_options.Method == SignInMethod.DeviceCode)
        {
            result = await DeviceCodeAsync(ct).ConfigureAwait(false);
        }
        else
        {
            try
            {
                result = await BrowserAsync(ct).ConfigureAwait(false);
            }
            catch (NoBrowserException)
            {
                _logger.LogInformation("{Prefix}: no browser could be opened; asking for a device-code sign-in instead", Prefix);
                result = await DeviceCodeAsync(ct).ConfigureAwait(false);
            }
        }

        _account = result.Account;
    }

    /// <summary>A bearer token for Graph, from the cache (MSAL renews it when it is about to expire).</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
    {
        var account = _account ?? throw new SignInRequiredException("not signed in");
        try
        {
            var result = await Silent(account, forceRefresh).ExecuteAsync(ct).ConfigureAwait(false);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            throw new SignInRequiredException($"the sign-in for {account.Username} has lapsed ({ex.ErrorCode}); restart the source to sign in again", ex);
        }
        catch (MsalServiceException ex) when (ex.ErrorCode is "invalid_grant" or "interaction_required")
        {
            throw new SignInRequiredException($"the sign-in for {account.Username} was revoked ({ex.ErrorCode}); restart the source to sign in again", ex);
        }
    }

    /// <summary>
    /// A silent request for the account's own tenant. With the authority left at "organizations" MSAL cannot
    /// match the cached token to a tenant and goes to the token endpoint instead, and a burst of those (the
    /// test suite starting many sources) trips its own throttling ("Your app has been throttled by AAD").
    /// </summary>
    private AcquireTokenSilentParameterBuilder Silent(IAccount account, bool forceRefresh)
    {
        var builder = _app.AcquireTokenSilent(_scopes, account).WithForceRefresh(forceRefresh);
        if (account.HomeAccountId?.TenantId is { Length: > 0 } tenantId)
        {
            builder = builder.WithTenantId(tenantId);
        }

        return builder;
    }

    private async Task<AuthenticationResult> BrowserAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(InteractiveTimeout);
        var builder = _app.AcquireTokenInteractive(_scopes)
            .WithUseEmbeddedWebView(false)
            .WithPrompt(_options.ForceLogin ? Prompt.ForceLogin : Prompt.SelectAccount)
            .WithSystemWebViewOptions(new SystemWebViewOptions
            {
                OpenBrowserAsync = uri =>
                {
                    if (!(_options.OpenBrowser ?? TryOpenBrowser)(uri))
                    {
                        throw new NoBrowserException();
                    }

                    _options.Progress("Waiting for the sign-in in your browser");
                    return Task.CompletedTask;
                },
                HtmlMessageSuccess = "<html><body style='font-family:sans-serif'><h3>AnyFS: signed in.</h3><p>You can close this tab.</p></body></html>",
                HtmlMessageError = "<html><body style='font-family:sans-serif'><h3>AnyFS: the sign-in failed.</h3><p>{0}: {1}</p></body></html>",
            });
        if (_options.LoginHint is { Length: > 0 } hint)
        {
            builder = builder.WithLoginHint(hint);
        }

        try
        {
            return await builder.ExecuteAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (NoBrowserException)
        {
            throw;
        }
        catch (Exception ex) when (ex.InnerException is NoBrowserException)
        {
            throw new NoBrowserException();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SignInRequiredException($"nobody finished the browser sign-in within {InteractiveTimeout.TotalMinutes:F0} minutes");
        }
        catch (MsalServiceException ex)
        {
            throw new SignInRequiredException(Describe(ex, _cloud, _options.ClientId, _options.Tenant), ex, IsPermanent(ex));
        }
        catch (MsalClientException ex)
        {
            throw new SignInRequiredException($"the browser sign-in failed: {ex.Message}", ex);
        }
    }

    private async Task<AuthenticationResult> DeviceCodeAsync(CancellationToken ct)
    {
        try
        {
            return await _app.AcquireTokenWithDeviceCode(_scopes, code =>
            {
                _options.Progress(code.Message);
                _logger.LogInformation("{Prefix}: {Message}", Prefix, code.Message);
                if (_options.Prompt is { } prompt)
                {
                    // shown, not awaited: MSAL is polling for the sign-in meanwhile
                    _ = prompt($"Sign in to {_options.Service.Name}", code.Message).ContinueWith(
                        t => _logger.LogDebug(t.Exception, "{Prefix}: the device-code prompt could not be shown", Prefix),
                        TaskContinuationOptions.OnlyOnFaulted);
                }

                return Task.CompletedTask;
            }).ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (MsalServiceException ex)
        {
            throw new SignInRequiredException(Describe(ex, _cloud, _options.ClientId, _options.Tenant), ex, IsPermanent(ex));
        }
    }

    /// <summary>
    /// Whether a refusal is about the settings or the organisation's policy (the codes <see cref="Describe"/> explains,
    /// except declines and expiries): starting the source again unchanged would be refused the same way, after
    /// opening the browser again, so it waits for the user's Retry instead.
    /// </summary>
    internal static bool IsPermanent(MsalServiceException ex)
        => AadstsCode(ex.Message) is 53000 or 53003 or 530035 or 50059 or 700016 or 50194 or 90002 or 900023 or 65001 or 90094 or 50011 or 7000218;

    /// <summary>
    /// What a refused sign-in means, in the settings' terms. The identity platform's AADSTS codes say which setting
    /// is wrong; the ones here are those an organisation's own registration, or the wrong cloud, runs into (the US
    /// Government endpoint answers AnyFS's own client ID with AADSTS50059: it has no such app).
    /// </summary>
    internal static string Describe(MsalServiceException ex, MicrosoftCloud cloud, string clientId, string tenant)
    {
        var message = ex.Message;
        var organizations = string.Equals(tenant, "organizations", StringComparison.OrdinalIgnoreCase);
        switch (AadstsCode(message))
        {
            case 53000 or 53003 or 530035:
                return "your organisation's Conditional Access policy blocked this sign-in (the device-code flow is often blocked: choose the browser sign-in in the settings)";
            case 50059 or 700016:
                return !cloud.HasShippedRegistration && string.Equals(clientId, DefaultClientId, StringComparison.OrdinalIgnoreCase)
                    ? $"AnyFS's own app registration exists only in Microsoft's global cloud, not the {cloud.Name} cloud: put your organisation's own registration's client ID in the settings"
                    : $"the app registration {clientId} does not exist in the {cloud.Name} cloud{(organizations ? "" : $" for tenant {tenant}")}: check the Client ID, Tenant and Cloud settings";
            case 50194:
                return $"the app registration {clientId} takes accounts of its own tenant only: put that tenant's domain or id in the Tenant setting";
            case 90002 or 900023:
                return $"tenant {tenant} was not found in the {cloud.Name} cloud: check the Tenant and Cloud settings";
            case 65001 or 90094:
                return $"an administrator must consent to the app registration {clientId} for the organisation before anyone can sign in with it";
            case 50011:
                return $"the app registration {clientId} does not list the redirect URI http://localhost: add it under the Mobile and desktop applications platform";
            case 7000218:
                return $"the app registration {clientId} is not set up as a public client: turn on Allow public client flows in its Authentication settings";
        }

        if (ex.ErrorCode == "authorization_declined" || ex.ErrorCode == "access_denied")
        {
            return "the sign-in was declined";
        }

        if (ex.ErrorCode == "expired_token" || ex.ErrorCode == "code_expired")
        {
            return "the device code expired before anyone used it";
        }

        var firstLine = message.Split('\n')[0].Trim();
        return $"the sign-in failed: {firstLine}";
    }

    /// <summary>The first AADSTS error code in an identity platform message (AADSTS50059: ... is 50059), or null.</summary>
    internal static int? AadstsCode(string message)
    {
        var at = message.IndexOf("AADSTS", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var start = at + "AADSTS".Length;
        var end = start;
        while (end < message.Length && char.IsAsciiDigit(message[end]))
        {
            end++;
        }

        return end > start && int.TryParse(message.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : null;
    }

    private static bool TryOpenBrowser(Uri uri)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
                return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", uri.AbsoluteUri)?.Dispose();
                return true;
            }

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            {
                return false;
            }

            Process.Start("xdg-open", uri.AbsoluteUri)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private void OnBeforeCacheAccess(TokenCacheNotificationArgs args)
    {
        if (!_cacheLoaded)
        {
            _cacheLoaded = true;
            // the copy this process refreshed most recently first: a stored copy can be older (another source,
            // or the tests, started from it), and starting from an expired token means a trip to the token
            // endpoint, which in a burst gets the app throttled
            _cache = RecentCache(_options.ClientId, _options.Tenant, _options.LoginHint) ?? _options.LoadCache();
        }

        if (_cache is not null)
        {
            args.TokenCache.DeserializeMsalV3(_cache, shouldClearExistingCache: true);
        }
    }

    private void OnAfterCacheAccess(TokenCacheNotificationArgs args)
    {
        if (!args.HasStateChanged)
        {
            return;
        }

        _cache = args.TokenCache.SerializeMsalV3();
        RecentCaches[RecentCacheKey(_options.ClientId, _options.Tenant, _options.LoginHint)] = _cache;
        try
        {
            _options.SaveCache(_cache);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "{Prefix}: the sign-in could not be saved; it will be asked for again next time", Prefix);
        }
    }

    private sealed class NoBrowserException : Exception;
}

/// <summary>An app sign-in, done: who it is, and its bearer tokens (true after a 401: a fresh one), from MSAL or a test's stand-in.</summary>
public sealed record AppSignIn(string? Username, Func<CancellationToken, bool, Task<string>> Token);
