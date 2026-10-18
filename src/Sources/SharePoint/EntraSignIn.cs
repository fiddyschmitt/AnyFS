using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using MsalLogLevel = Microsoft.Identity.Client.LogLevel;

namespace AnyFS.Sources.SharePoint;

/// <summary>How the user signs in when there is no token to reuse.</summary>
public enum SignInMethod
{
    /// <summary>A sign-in page in the system browser, with the reply caught on a loopback port. Falls back to a device code when no browser can be opened.</summary>
    Browser,
    /// <summary>A code to type at microsoft.com/devicelogin on any device. Organisations may block it (spike 9: the sandbox did).</summary>
    DeviceCode,
}

/// <summary>What a sign-in needs from its caller: the app and tenant, how to talk to the user, and where the token cache lives.</summary>
/// <param name="Scopes">The delegated Microsoft Graph scopes to ask for: <see cref="EntraSignIn.SiteScopes"/> or <see cref="EntraSignIn.ContainerScopes"/>.</param>
/// <param name="Progress">One-line status while signing in ("Waiting for the sign-in in your browser").</param>
/// <param name="Prompt">Puts a message in front of the user without waiting for an answer (the device code), or null when nobody can be asked.</param>
/// <param name="LoadCache">The serialised MSAL cache from the last run, or null.</param>
/// <param name="SaveCache">Stores the serialised MSAL cache after it changed.</param>
/// <param name="OpenBrowser">Shows the sign-in page: false when it could not (the device code is used instead). Null: the system browser.</param>
/// <param name="ForceLogin">The browser sign-in asks for credentials rather than offering the browser's signed-in accounts (for signing in as exactly the named account).</param>
internal sealed record SignInOptions(
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
    bool ForceLogin = false);

/// <summary>Thrown when a token can't be had without the user signing in again (the refresh token expired or was revoked).</summary>
public sealed class SignInRequiredException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The Microsoft identity platform sign-in behind the SharePoint Online and Embedded sources, on MSAL. Silent
/// first (the cache from the last run, refresh tokens included); otherwise the system browser or a device
/// code, as the settings say. The cache is handed back to the caller serialised, so the source keeps it in its
/// secret store; a process-wide copy also bridges the settings dialog's Test button and the source it then
/// starts, so the user signs in once.
/// </summary>
internal sealed class EntraSignIn
{
    /// <summary>The delegated Microsoft Graph scopes for a SharePoint Online site; the app registration lists the same (provisioning/sharepoint/register-app.ps1).</summary>
    public static readonly string[] SiteScopes = ["User.Read", "Files.ReadWrite.All", "Sites.ReadWrite.All"];

    /// <summary>The delegated Microsoft Graph scopes for SharePoint Embedded containers (spec 7.5): the one scope Embedded uses, which the container type's registration then narrows per app.</summary>
    public static readonly string[] ContainerScopes = ["User.Read", "FileStorageContainer.Selected"];

    private static readonly TimeSpan InteractiveTimeout = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, byte[]> RecentCaches = new(StringComparer.Ordinal);

    private readonly SignInOptions _options;
    private readonly ILogger _logger;
    private readonly IPublicClientApplication _app;
    private byte[]? _cache;
    private bool _cacheLoaded;
    private IAccount? _account;

    public EntraSignIn(SignInOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
        var builder = PublicClientApplicationBuilder.Create(options.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, options.Tenant)
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
                    _logger.LogDebug("sharepoint: signed in silently as {Account} (token from {Source})", silent.Account.Username, silent.AuthenticationResultMetadata.TokenSource);
                    return;
                }
                catch (MsalThrottledUiRequiredException ex) when (attempt <= 4)
                {
                    // MSAL replays a recent refusal for a while rather than ask the server again; that is not a
                    // reason to make the user sign in, so wait it out before deciding
                    _logger.LogInformation("sharepoint: the sign-in for {Account} is throttled ({Code}); waiting 30 s before trying again", preferred.Username, ex.ErrorCode);
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                }
                catch (MsalUiRequiredException ex)
                {
                    _logger.LogInformation("sharepoint: the saved sign-in for {Account} needs renewing ({Code}: {Message})", preferred.Username, ex.ErrorCode, ex.Message.Split('\n')[0].Trim());
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
                _logger.LogInformation("sharepoint: no browser could be opened; asking for a device-code sign-in instead");
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
        var builder = _app.AcquireTokenSilent(_options.Scopes, account).WithForceRefresh(forceRefresh);
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
        var builder = _app.AcquireTokenInteractive(_options.Scopes)
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
            throw new SignInRequiredException(Describe(ex), ex);
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
            return await _app.AcquireTokenWithDeviceCode(_options.Scopes, code =>
            {
                _options.Progress(code.Message);
                _logger.LogInformation("sharepoint: {Message}", code.Message);
                if (_options.Prompt is { } prompt)
                {
                    // shown, not awaited: MSAL is polling for the sign-in meanwhile
                    _ = prompt("Sign in to SharePoint", code.Message).ContinueWith(
                        t => _logger.LogDebug(t.Exception, "sharepoint: the device-code prompt could not be shown"),
                        TaskContinuationOptions.OnlyOnFaulted);
                }

                return Task.CompletedTask;
            }).ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (MsalServiceException ex)
        {
            throw new SignInRequiredException(Describe(ex), ex);
        }
    }

    private static string Describe(MsalServiceException ex)
    {
        var message = ex.Message;
        if (message.Contains("AADSTS53003", StringComparison.Ordinal) || message.Contains("AADSTS530035", StringComparison.Ordinal) || message.Contains("AADSTS53000", StringComparison.Ordinal))
        {
            return "your organisation's Conditional Access policy blocked this sign-in (the device-code flow is often blocked: choose the browser sign-in in the settings)";
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
            _logger.LogWarning(ex, "sharepoint: the sign-in could not be saved; it will be asked for again next time");
        }
    }

    private sealed class NoBrowserException : Exception;
}
