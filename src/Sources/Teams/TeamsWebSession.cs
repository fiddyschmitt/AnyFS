using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Teams;

/// <summary>
/// The web session's tokens for the Teams services (spec 7.9, spike 18): a browser of AnyFS's own, on a profile per
/// account, opens Teams on the web; the page's own calls to the chat service, the aggregator and the middle tier carry a
/// bearer per service that Microsoft's first-party Teams web client minted for the signed-in account, which AnyFS reads
/// off those requests through DevTools and uses itself — the same way the Outlook web session (<c>OwaTokenSession</c>)
/// reads the one bearer OWA mints. Where the Chromium browser's DevTools cannot be reached (a policy that fixes its
/// profile folder or turns remote debugging off, as on a work machine), or the Browser setting names Firefox, the page is
/// opened in Firefox through Selenium WebDriver instead, whose WebDriver BiDi events give the same requests
/// (<see cref="FirefoxBiDiBrowser"/>). A headless launch gets them without a window while the profile's sign-in holds; a
/// window is opened for the person only when it has lapsed and the caller allows. A token for a personal Microsoft account,
/// or for another organisation than the first token read, is never read; a sign-in name other than the account the source
/// was given is read (a work account's is often not its email) and logged.
/// </summary>
internal sealed class TeamsWebSession(Uri teamsHost, string browserPath, string profileDir, string expectedAccount, ILogger logger)
{
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SilentDeadline = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan InteractiveDeadline = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SharePointGrace = TimeSpan.FromSeconds(5);
    // a background load waiting for a token the page mints only as it gets round to it: long, as nothing waits on it
    private static readonly TimeSpan OptionalDeadline = TimeSpan.FromSeconds(90);

    // the services every sign-in reads, one bearer each (spike 18): matched by audience host, so any cloud's; SharePoint's
    // and Graph's are kept when the page sends them, and asked for only in the background (SeekAsync)
    private static readonly TeamsService[] Wanted = TeamsServices.Core;

    private readonly ConcurrentDictionary<TeamsService, (string Token, DateTimeOffset Expires)> _tokens = new();

    // tokens a service refused, never kept again (Refused)
    private readonly ConcurrentDictionary<string, byte> _refused = new(StringComparer.Ordinal);

    // the organisation of the first token read: another's is never read (Take); personal Microsoft accounts' tenant
    private const string PersonalTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";
    private string? _tenant;
    private int _warned;

    // Firefox, for opening the page through WebDriver: the configured browser when it is Firefox, else the installed one
    private readonly string? _firefox = BrowserLocator.KindOf(browserPath) == BrowserKind.Firefox ? browserPath : BrowserLocator.FindKind(BrowserKind.Firefox);

    // whether the page is opened in Firefox through WebDriver: from the start when the setting names Firefox, else from
    // the first launch whose DevTools cannot be reached
    private bool _viaFirefox = BrowserLocator.KindOf(browserPath) == BrowserKind.Firefox;

    public string BrowserName => _viaFirefox ? BrowserLocator.DisplayName(BrowserKind.Firefox) : BrowserLocator.DisplayName(browserPath);

    /// <summary>The account the tokens are for, from their claims; null before any has been read.</summary>
    public string? Account { get; private set; }

    /// <summary>The account's own mri (<c>8:orgid:&lt;object id&gt;</c>), from a token's object-id claim.</summary>
    public string? Mri { get; private set; }

    private string StartPage => $"{teamsHost.AbsoluteUri.TrimEnd('/')}/v2/?login_hint={Uri.EscapeDataString(expectedAccount)}";

    /// <summary>A bearer for <paramref name="service"/> good for at least ten more minutes; captures a fresh set from the browser when none is in hand.</summary>
    public async Task<string> TokenAsync(TeamsService service, bool interactive, Action<string> progress, CancellationToken ct)
    {
        if (Fresh(service) is { } kept)
        {
            return kept;
        }

        var gate = ProfileLocks.For(profileDir);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Fresh(service) is { } again)
            {
                return again;
            }

            if (!_viaFirefox && BrowserLocator.DevToolsBlocked(browserPath) is { } blocked)
            {
                UseFirefox(blocked);
            }

            var silent = await CaptureAsync(headless: true, SilentDeadline, [service], ct).ConfigureAwait(false);
            if (silent.Final && silent.Failure is { } fatal)
            {
                throw new SignInRequiredException(fatal, permanent: true);
            }

            if (Fresh(service) is { } fresh)
            {
                return fresh;
            }

            if (Wanted.All(w => Fresh(w) is not null))
            {
                // signed in, but the page did not hand out this one (SharePoint's, for files) in time: not a lapsed sign-in
                throw new SignInRequiredException($"Teams on the web did not hand out its {service} token in time; try again", permanent: false);
            }

            if (!interactive)
            {
                throw new SignInRequiredException($"the sign-in to {teamsHost.Host} has lapsed; sign in again (open the source, or its Retry)", permanent: false);
            }

            logger.LogInformation("teams: no sign-in to read for {Host} without a window ({Why}); opening {Browser} for the person to sign in", teamsHost.Host, silent.Failure, BrowserName);
            progress($"Sign in to Teams in the {BrowserName} window that opened");
            var shown = await CaptureAsync(headless: false, InteractiveDeadline, [service], ct).ConfigureAwait(false);
            return Fresh(service) ?? throw new SignInRequiredException(shown.Failure ?? $"no sign-in to {teamsHost.Host} was read", permanent: shown.Final);
        }
        finally
        {
            gate.Release();
        }
    }

    // the services a load waited for in vain (SharePoint's where a headless load did not use it, Graph's where the client
    // mints it only as it gets round to it), until when none is waited for again, and the wait after the next in vain
    // (doubling, to eight hours); forgotten once the service's token is read
    private readonly ConcurrentDictionary<TeamsService, (DateTimeOffset Until, TimeSpan Next)> _absent = new();

    /// <summary>A bearer for <paramref name="service"/> good for at least ten more minutes, if one is in hand; null, never a wait, if not.</summary>
    public string? InHand(TeamsService service) => Fresh(service);

    /// <summary>
    /// For the background alone (nothing waits on it): one headless load of the page, waiting a while for the tokens of
    /// <paramref name="services"/> it mints only as it gets round to them (SharePoint's; Graph's in the US Gov clouds) and
    /// that are not in hand; true when one came. One that does not come is not waited for again for half an hour, then an
    /// hour, two... up to eight. No load while the core tokens near their renewal (the load holds the profile, which their
    /// renewal would wait on); never a sign-in window, never a failure.
    /// </summary>
    public async Task<bool> SeekAsync(IReadOnlyCollection<TeamsService> services, CancellationToken ct)
    {
        List<TeamsService> Missing()
        {
            var now = DateTimeOffset.UtcNow;
            return services.Where(s => Fresh(s) is null && !(_absent.TryGetValue(s, out var absent) && absent.Until > now)).ToList();
        }

        var lacking = services.Where(s => Fresh(s) is null).ToList();
        if (Missing().Count == 0 || Wanted.Any(w => Fresh(w, Margin + OptionalDeadline + Margin) is null))
        {
            return false;
        }

        var gate = ProfileLocks.For(profileDir);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var missing = Missing();
            if (missing.Count > 0)
            {
                try
                {
                    await CaptureAsync(headless: true, OptionalDeadline, missing, ct).ConfigureAwait(false);
                }
                catch (SignInRequiredException ex)
                {
                    logger.LogInformation("teams: no {Services} token without a sign-in ({Error})", string.Join(" or ", missing), ex.Message);
                }
            }

            foreach (var service in missing.Where(s => Fresh(s) is null))
            {
                var next = _absent.TryGetValue(service, out var before) ? before.Next : TimeSpan.FromMinutes(30);
                logger.LogInformation("teams: Teams on the web gave no {Service} token in a {Seconds:F0} s load; asked again in {Minutes:F0} min", service, OptionalDeadline.TotalSeconds, next.TotalMinutes);
                _absent[service] = (DateTimeOffset.UtcNow + next, TimeSpan.FromTicks(Math.Min(next.Ticks * 2, TimeSpan.FromHours(8).Ticks)));
            }

            // this load's, or another's that came meanwhile
            return lacking.Any(s => Fresh(s) is not null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// A token a service refused before its expiry (revoked, or a claims challenge): forgotten if it is still the one kept —
    /// a fresh one read meanwhile stays — and not taken from the page again, which can go on sending it until it renews it
    /// itself, so the next ask waits for the renewed one.
    /// </summary>
    public void Refused(TeamsService service, string token)
    {
        if (_refused.Count > 64)
        {
            _refused.Clear();
        }

        _refused[token] = 0;
        if (_tokens.TryGetValue(service, out var kept) && kept.Token == token)
        {
            _tokens.TryRemove(new KeyValuePair<TeamsService, (string Token, DateTimeOffset Expires)>(service, kept));
        }
    }

    private string? Fresh(TeamsService service) => Fresh(service, Margin);

    private string? Fresh(TeamsService service, TimeSpan margin) => _tokens.TryGetValue(service, out var t) && t.Expires - DateTimeOffset.UtcNow > margin ? t.Token : null;

    private void Keep(string token)
    {
        if (_refused.ContainsKey(token))
        {
            return;
        }

        var (aud, account, mri, _, expires) = Claims(token);
        if (aud is null || expires is null)
        {
            return;
        }

        if (TeamsServices.Of(aud) is not { } service)
        {
            return;
        }

        Account ??= account;
        Mri ??= mri;
        _tokens[service] = (token, expires.Value);
        _absent.TryRemove(service, out _);
    }

    /// <summary>
    /// Keeps a bearer the page sent, unless it is a personal Microsoft account's or another organisation's than the first one
    /// read, which are never read: who it is for then, else null. A sign-in name other than the account given is read — a
    /// work account's is often not its email — and logged once.
    /// </summary>
    internal string? Take(string bearer, string? url = null)
    {
        var (aud, account, _, tenant, _) = Claims(bearer);
        if (tenant is not null)
        {
            if (tenant.Equals(PersonalTenant, StringComparison.OrdinalIgnoreCase))
            {
                return $"{account ?? "an account"}, a personal Microsoft account";
            }

            var first = Interlocked.CompareExchange(ref _tenant, tenant, null) ?? tenant;
            if (!tenant.Equals(first, StringComparison.OrdinalIgnoreCase))
            {
                return $"{account ?? "an account"}, of another organisation";
            }
        }

        if (account is not null && !account.Equals(expectedAccount, StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning("teams: the {Browser} profile for {Expected} is signed in as {Account}; reading it, as a work account's sign-in name is often not its email", BrowserName, expectedAccount, account);
        }

        Keep(bearer);

        // where the page used SharePoint's token is the tenant's SharePoint (its OneDrive host, as it loads)
        if (SharePointHost is null && TeamsServices.Of(aud) == TeamsService.SharePoint && Uri.TryCreate(url, UriKind.Absolute, out var used) && TeamsServices.IsSharePointHost(used.Host))
        {
            SharePointHost = used.Host;
        }

        return null;
    }

    /// <summary>The host the page used SharePoint's token on (the tenant's OneDrive or SharePoint), once seen; the files are found from it.</summary>
    public string? SharePointHost { get; private set; }

    /// <summary>Opens the page in Firefox through WebDriver from now on, the Chromium browser's DevTools being out of reach; fails when there is no Firefox.</summary>
    private void UseFirefox(string why)
    {
        if (_firefox is null)
        {
            throw new SignInRequiredException($"The web session cannot read the sign-in from {BrowserName}: {why}. With Firefox installed, AnyFS signs in through it instead.", permanent: true);
        }

        logger.LogInformation("teams: {Browser} gives AnyFS no DevTools here ({Why}); signing in through Firefox's WebDriver from now on", BrowserName, why);
        _viaFirefox = true;
    }

    /// <summary>What one browser launch came to: whether a fresh set was captured, else why not and whether trying again can help.</summary>
    private sealed record Capture(bool Got, string? Failure = null, bool Final = false);

    /// <summary>One capture: done once the core services' tokens and <paramref name="want"/>'s are in hand.</summary>
    private async Task<Capture> CaptureAsync(bool headless, TimeSpan deadline, IReadOnlyCollection<TeamsService> want, CancellationToken ct)
    {
        if (!_viaFirefox)
        {
            var (capture, noDevTools) = await CaptureInChromiumAsync(headless, deadline, want, ct).ConfigureAwait(false);
            if (noDevTools is null || _firefox is null)
            {
                return capture;
            }

            UseFirefox(noDevTools);
        }

        return await CaptureInFirefoxAsync(headless, deadline, want, ct).ConfigureAwait(false);
    }

    /// <summary>One capture in the Chromium browser over DevTools; with why there was no DevTools when the launch gave none.</summary>
    private async Task<(Capture Capture, string? NoDevTools)> CaptureInChromiumAsync(bool headless, TimeSpan deadline, IReadOnlyCollection<TeamsService> want, CancellationToken ct)
    {
        var mode = headless ? "headless" : "with a window";
        DevToolsBrowser? browser = null;
        try
        {
            var launch = await DevToolsBrowser.LaunchAsync(browserPath, profileDir, headless, "about:blank", [], logger, "teams", ct).ConfigureAwait(false);
            if (launch.Browser is null)
            {
                return (new Capture(false, launch.Failure, Final: true), launch.Failure ?? "it opened no DevTools port");
            }

            browser = launch.Browser;
            var cdp = browser.Client;
            logger.LogInformation("teams: opened {Browser} {Mode} for {Host} (DevTools on port {Port})", BrowserName, mode, teamsHost.Host, browser.Port);

            var pages = new ConcurrentDictionary<string, string>();
            string? wrongAccount = null;
            cdp.Event += (method, p, sessionId) =>
            {
                switch (method)
                {
                    case "Target.attachedToTarget":
                        {
                            var sid = p["sessionId"]!.GetValue<string>();
                            if (p["targetInfo"]?["type"]?.GetValue<string>() == "page")
                            {
                                pages[sid] = p["targetInfo"]?["url"]?.GetValue<string>() ?? "";
                            }

                            _ = cdp.SendAsync("Network.enable", new JsonObject(), sid, CancellationToken.None);
                            _ = cdp.SendAsync("Target.setAutoAttach", new JsonObject { ["autoAttach"] = true, ["waitForDebuggerOnStart"] = false, ["flatten"] = true }, sid, CancellationToken.None);
                            break;
                        }

                    case "Target.targetInfoChanged":
                        {
                            if (p["targetInfo"]?["type"]?.GetValue<string>() == "page")
                            {
                                foreach (var key in pages.Keys)
                                {
                                    pages[key] = p["targetInfo"]?["url"]?.GetValue<string>() ?? "";
                                }
                            }

                            break;
                        }

                    case "Network.requestWillBeSent":
                    case "Network.requestWillBeSentExtraInfo":
                        {
                            var headers = (p["request"]?["headers"] ?? p["headers"]) as JsonObject;
                            var auth = headers?.FirstOrDefault(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>();
                            if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && Take(auth[7..], p["request"]?["url"]?.GetValue<string>()) is { } other)
                            {
                                wrongAccount = other;
                            }

                            break;
                        }
                }
            };
            await cdp.SendAsync("Target.setDiscoverTargets", new JsonObject { ["discover"] = true }, ct: ct).ConfigureAwait(false);
            await cdp.SendAsync("Target.setAutoAttach", new JsonObject { ["autoAttach"] = true, ["waitForDebuggerOnStart"] = false, ["flatten"] = true }, ct: ct).ConfigureAwait(false);

            string? page = null;
            for (var i = 0; i < 100 && page is null; i++)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                page = pages.Keys.FirstOrDefault();
            }

            if (page is null)
            {
                return (new Capture(false, $"{BrowserName} opened no page", Final: headless), null);
            }

            await Task.Delay(300, ct).ConfigureAwait(false);
            await cdp.SendAsync("Page.navigate", new JsonObject { ["url"] = StartPage }, page, ct).ConfigureAwait(false);
            var capture = await WaitAsync(headless, deadline, want, () => wrongAccount, () => pages.Values.FirstOrDefault(u => u.Length > 0) ?? "", () => pages.IsEmpty, ct).ConfigureAwait(false);
            return (capture, null);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or TimeoutException)
        {
            return (new Capture(false, $"{BrowserName} ({mode}) closed before the sign-in to {teamsHost.Host} was read: {ex.Message}", Final: !headless), null);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return (new Capture(false, $"{BrowserName} answered AnyFS in a form it does not know ({ex.Message})", Final: true), null);
        }
        finally
        {
            if (browser is not null)
            {
                await browser.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>One capture in Firefox through WebDriver: the page's requests come from its WebDriver BiDi events.</summary>
    private async Task<Capture> CaptureInFirefoxAsync(bool headless, TimeSpan deadline, IReadOnlyCollection<TeamsService> want, CancellationToken ct)
    {
        var mode = headless ? "headless" : "with a window";
        string? wrongAccount = null;
        var launch = await FirefoxBiDiBrowser.LaunchAsync(_firefox!, $"{profileDir}-firefox", headless, (url, authorization) =>
        {
            if (authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && Take(authorization[7..], url) is { } other)
            {
                wrongAccount = other;
            }
        }, ct).ConfigureAwait(false);
        if (launch.Browser is not { } browser)
        {
            return new Capture(false, launch.Failure, Final: true);
        }

        try
        {
            logger.LogInformation("teams: opened Firefox through WebDriver {Mode} for {Host}", mode, teamsHost.Host);
            browser.Navigate(StartPage);
            return await WaitAsync(headless, deadline, want, () => wrongAccount, () => browser.Page ?? "", () => !browser.Open, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return new Capture(false, $"Firefox ({mode}) closed before the sign-in to {teamsHost.Host} was read: {ex.Message}", Final: !headless);
        }
        finally
        {
            await browser.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits while the page signs in and calls the services: done once every service's token is in hand; otherwise what
    /// the wait came to — another account's tokens, a headless launch held on the sign-in page (no saved sign-in), the
    /// window closed, or the deadline (with a window, some tokens are enough to go on with).
    /// </summary>
    private async Task<Capture> WaitAsync(bool headless, TimeSpan deadline, IReadOnlyCollection<TeamsService> want, Func<string?> wrongAccount, Func<string> where, Func<bool> closed, CancellationToken ct)
    {
        var mode = headless ? "headless" : "with a window";
        var until = DateTime.UtcNow + deadline;
        var onSignInSince = (DateTime?)null;
        var graceSince = (DateTime?)null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (wrongAccount() is { } other)
            {
                return new Capture(false, $"the {BrowserName} profile is signed in as {other}, not {expectedAccount}; sign in as {expectedAccount}", Final: true);
            }

            if (Wanted.All(w => Fresh(w) is not null) && want.All(w => Fresh(w) is not null))
            {
                // SharePoint's token (the files) usually follows the others within seconds: a few more, so the first file
                // read needs no second launch
                graceSince ??= DateTime.UtcNow;
                if (Fresh(TeamsService.SharePoint) is not null || DateTime.UtcNow - graceSince > SharePointGrace)
                {
                    return new Capture(true);
                }
            }

            var page = where();
            if (headless && (page.Contains("login.microsoftonline.", StringComparison.OrdinalIgnoreCase) || page.Contains("login.live.com", StringComparison.OrdinalIgnoreCase)))
            {
                onSignInSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - onSignInSince > TimeSpan.FromSeconds(8))
                {
                    return new Capture(false, "no saved sign-in");
                }
            }

            if (!headless && closed())
            {
                return new Capture(false, $"the {BrowserName} window was closed before the sign-in to {teamsHost.Host} finished; Retry to sign in", Final: true);
            }

            if (DateTime.UtcNow > until)
            {
                logger.LogInformation("teams: {Count}/{Total} service tokens after {Seconds:F0}s {Mode}; the page is at {Where}", Wanted.Count(w => Fresh(w) is not null), Wanted.Length, deadline.TotalSeconds, mode, page);
                return Wanted.Any(w => Fresh(w) is not null) && !headless
                    ? new Capture(true)
                    : new Capture(false, headless ? "no saved sign-in" : $"nobody finished the sign-in to {teamsHost.Host} in time; Retry to sign in", Final: !headless);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
    }

    /// <summary>A JWT's audience, the account (upn/preferred_username), the account's mri (<c>8:orgid:&lt;oid&gt;</c>), its tenant and expiry; nulls for anything else.</summary>
    internal static (string? Aud, string? Account, string? Mri, string? Tenant, DateTimeOffset? Expires) Claims(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
            {
                return (null, null, null, null, null);
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = json.RootElement;
            string? S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var aud = S("aud");
            var account = S("upn") ?? S("preferred_username") ?? S("unique_name");
            var oid = S("oid");
            var mri = oid is null ? null : $"8:orgid:{oid}";
            var expires = root.TryGetProperty("exp", out var e) && e.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : (DateTimeOffset?)null;
            return (aud, account, mri, S("tid"), expires);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return (null, null, null, null, null);
        }
    }
}
