using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Artifactory;

/// <summary>
/// What a test (or the Test button) changes about an Artifactory session: what sends its requests; for the web session,
/// what stands in for the person at the browser (given the platform's sign-in page for the session, whether a window may be
/// shown, and what ends when AnyFS closes the window: signs in there and gives the code, or does not, or closes the
/// window by finishing), and how long the platform holds a session for them (null for the real platform's five minutes);
/// and whether a sign-in kept from before is used.
/// </summary>
internal sealed record ArtifactoryStandIn(Func<HttpMessageHandler> Handler, Func<bool, Uri, CancellationToken, Task>? Browser = null, bool UseKept = true, TimeSpan? SessionHeld = null);

/// <summary>
/// A token JFrog's client sign-in gave: its access token, which the whole platform takes, the refresh token that renews it
/// (once: a renewal gives the next), and the account it names, where the platform said.
/// </summary>
internal sealed record ArtifactoryClientToken(string Access, string? Refresh, string? User)
{
    /// <summary>When the access token runs out (its <c>exp</c>); null for one that does not say.</summary>
    public DateTimeOffset? Until => ArtifactorySession.ExpiryOf(Access);

    /// <summary>When the access token was made (its <c>iat</c>); null for one that does not say.</summary>
    public DateTimeOffset? Issued => ArtifactorySession.IssuedOf(Access);

    /// <summary>The token in an answer of the platform's (the client sign-in's, a renewal's); null for one that holds none.</summary>
    public static ArtifactoryClientToken? From(JsonElement answer)
        => answer.ValueKind == JsonValueKind.Object && answer.Str("access_token") is { Length: > 0 } access
            ? new ArtifactoryClientToken(access, answer.Str("refresh_token"), answer.Str("username"))
            : null;

    /// <summary>A token kept as <see cref="ToJson"/> wrote it; null for anything else.</summary>
    public static ArtifactoryClientToken? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return From(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => new JsonObject { ["access_token"] = Access, ["refresh_token"] = Refresh, ["username"] = User }.ToJsonString();
}

/// <summary>
/// An Artifactory source's sign-in and the client its requests go through (spec 7.13). First it finds Artifactory behind
/// the address (where its ping answers: <c>/artifactory</c> on the host, the host's root, or a path of its own); then it
/// signs in the way the settings say. A token goes on every request as a bearer token, an API key in the header
/// Artifactory reads one from, a user name and password as Basic; a refusal of any of them is final. The web session signs
/// in through JFrog's client sign-in (<see cref="ArtifactorySignInTarget"/>), the one JFrog's own <c>jf login</c> makes:
/// the person signs in on the platform's own page in a browser of AnyFS's own and gives it the code AnyFS shows, within the
/// five minutes the platform holds the sign-in for, and the platform hands AnyFS a token of its own, which goes on every
/// request as a bearer token and is renewed with the refresh token that came with it, before it runs out and when it is
/// refused. Kept in the source's secret store, it signs in at the next start with nobody at the browser. The client follows no redirects itself: a file's download sent on to a
/// storage service's address goes there without the sign-in.
/// </summary>
internal sealed class ArtifactorySession : IDisposable, IRequestSession
{
    /// <summary>The secret the web session's token is kept in for the next start.</summary>
    public const string TokenSecret = "artifactory-client-token";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinRenewalInterval = TimeSpan.FromSeconds(30);

    // the token of a sign-in the Test just made, by installation, for the source started from the same dialog: taken once,
    // since a refresh token renews once, and two sessions holding one would leave the second with one already spent

    private readonly ILogger _logger;
    private readonly KeptSignIn _kept;
    private readonly ArtifactoryStandIn? _standIn;
    private readonly HttpMessageHandler _handler;
    private readonly SignInRenewal _renewal;
    private bool _signedIn;

    // why the web session cannot be renewed any more, once Artifactory has refused a renewal
    private string? _lapsed;

    // the web session's token as it stands
    private ArtifactoryClientToken? _client;

    // how far the server's clock is ahead of this computer's, from the Date of its answers
    private long _clockAheadTicks;

    /// <param name="watcher">Who hears of each request made (<see cref="ISourceContext.UpstreamWatcher"/>).</param>
    public ArtifactorySession(ArtifactorySettings settings, ILogger logger, ArtifactoryStandIn? standIn = null, ISecretStore? secrets = null, IUpstreamWatcher? watcher = null)
    {
        Settings = settings;
        _logger = logger;
        _kept = new KeptSignIn(secrets, TokenSecret, "artifactory", logger);
        _standIn = standIn;
        Url = settings.Url ?? throw new SourceFailedException("The address is not an http or https address.", permanent: true);
        Method = settings.EffectiveSignIn;
        HttpMessageHandler inner;
        if (standIn is not null)
        {
            inner = standIn.Handler();
        }
        else
        {
            var sockets = new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            };
            if (settings.TrustAnyCertificate)
            {
                sockets.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }

            inner = sockets;
        }

        _handler = new ClockHandler(this, UpstreamRequestsHandler.Around(inner, watcher));
        Http = new HttpClient(_handler, disposeHandler: false) { Timeout = RequestTimeout };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"AnyFS/{typeof(ArtifactorySession).Assembly.GetName().Version?.ToString(3) ?? "0"}");
        Address = ArtifactoryAddress.Candidates(Url)[0];
        _renewal = new SignInRenewal(RefreshAgainAsync, MinRenewalInterval);
        _renewal.Renewed += () => Renewed?.Invoke();
    }

    public ArtifactorySettings Settings { get; }

    /// <summary>The address as given, tidied.</summary>
    public Uri Url { get; }

    public ArtifactorySignIn Method { get; }

    /// <summary>Artifactory's address and what the rest of the address names, once <see cref="FindServerAsync"/> has found it.</summary>
    public ArtifactoryAddress Address { get; private set; }

    /// <summary>The platform's address (its pages at <c>ui/</c>, its access service at <c>access/</c>): what Artifactory's own is below.</summary>
    public Uri Platform => new(Address.Base, "../");

    /// <summary>Artifactory's version, where it told it.</summary>
    public string? Version { get; private set; }

    /// <summary>Who is signed in, once signed in, where the server or the sign-in tells: null for a token that does not name its account.</summary>
    public string? User { get; private set; }

    /// <summary>For every request: no redirects followed.</summary>
    public HttpClient Http { get; }

    /// <summary>How the source signed in, for messages ("with an access token", "with Microsoft Edge").</summary>
    public string How { get; private set; } = "";

    /// <summary>The account for messages: its name, or what it signs in with where that does not name it.</summary>
    public string Who => User ?? Settings.Who;

    /// <summary>
    /// What tells one account's kept listings from another's: its name, or, for a token that names none, a few characters of
    /// the token's hash (another token may be another account's, and sees other files).
    /// </summary>
    public string Account => User
        ?? (Method == ArtifactorySignIn.Token && Settings.Token is { } token
            ? "token:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..12]
            : Method.ToString());

    /// <summary>How many times the sign-in has been renewed: a request remembers it, so one renewal serves every request that lapsed with it.</summary>
    public int Generation => _renewal.Generation;

    /// <summary>Whether a refused request may be signed in again: only the web session's, whose token renews.</summary>
    public bool CanRenew => Method == ArtifactorySignIn.WebSession;

    /// <summary>The time on the server's clock now, as its answers' dates tell it: what its files' times are compared with.</summary>
    public DateTimeOffset ServerNow => DateTimeOffset.UtcNow.AddTicks(Volatile.Read(ref _clockAheadTicks));

    /// <summary>
    /// Finds Artifactory behind the address: the first of its candidates whose ping answers <c>OK</c>, as Artifactory's does
    /// to anyone, signed in or not. Where none does (a proxy before the server that lets nothing through unsigned), the first
    /// whose version call answers the sign-in with a version; else the likeliest, and the sign-in says what it met there.
    /// </summary>
    public async Task FindServerAsync(CancellationToken ct)
    {
        var candidates = ArtifactoryAddress.Candidates(Url);
        try
        {
            foreach (var candidate in candidates)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, candidate.Api("system/ping"));
                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim() == "OK")
                {
                    Found(candidate);
                    return;
                }
            }

            foreach (var candidate in candidates)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, candidate.Api("system/version"));
                await AuthorizeAsync(request, ct).ConfigureAwait(false);
                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                if (await IsJsonOfAsync(response, e => e.ValueKind == JsonValueKind.Object && e.Str("version") is { Length: > 0 }, ct).ConfigureAwait(false))
                {
                    Found(candidate);
                    return;
                }
            }
        }
        catch (Exception ex) when (IsNoAnswer(ex, ct))
        {
            throw new SourceFailedException($"Artifactory ({Url.Host}) did not answer: {ex.Message}", inner: ex);
        }

        Address = candidates[0];
        _logger.LogInformation("artifactory: nothing below {Url} answered as Artifactory does; taking {Base}", Url, Address.Base);
    }

    private void Found(ArtifactoryAddress address)
    {
        Address = address;
        _logger.LogInformation("artifactory: Artifactory {Version} at {Base}, {Scope}", Version ?? "(version not told)", address.Base,
            address.Scope == ArtifactoryScope.Server ? "the whole server" : address.Repository + (address.Path.Length == 0 ? "" : "/" + address.Path));
    }

    /// <summary>A request the server never answered: the network's failure, or a time out that was not the caller's.</summary>
    private static bool IsNoAnswer(Exception ex, CancellationToken ct) => ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested;

    /// <summary>Whether an answer is JSON that <paramref name="holds"/> what was asked for.</summary>
    private static async Task<bool> IsJsonOfAsync(HttpResponseMessage response, Func<JsonElement, bool> holds, CancellationToken ct)
    {
        using var doc = await JsonOfAsync(response, ct).ConfigureAwait(false);
        return doc is not null && holds(doc.RootElement);
    }

    /// <summary>A successful answer's JSON; null where the answer failed or is not JSON.</summary>
    private static async Task<JsonDocument?> JsonOfAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Signs in for the source's start or the Test: the way the settings say; a web session's token kept from before (or
    /// just made by the Test) while Artifactory takes it, renewed where it must be, else JFrog's client sign-in in a browser,
    /// which needs the person at its window and so is tried only when <paramref name="interactive"/>. Throws
    /// <see cref="SourceFailedException"/> (permanent for a refusal only new settings can change) when no sign-in can be had.
    /// </summary>
    public async Task SignInAsync(bool interactive, Action<string> progress, CancellationToken ct)
    {
        progress($"Signing in to {Url.Host}");
        await FindServerAsync(ct).ConfigureAwait(false);
        switch (Method)
        {
            case ArtifactorySignIn.Token:
                if (Settings.Token is null)
                {
                    throw new SourceFailedException("Give an access token (Sign in with: Access token), or choose another way to sign in.", permanent: true);
                }

                await CheckAsync("the access token: it may have expired or been revoked", ct).ConfigureAwait(false);
                User = NameIn(Settings.Token) ?? await NameFromPlatformAsync(ct).ConfigureAwait(false);
                How = IsApiKey(Settings.Token) ? "with an API key" : "with an access token";
                break;

            case ArtifactorySignIn.Password:
                if (Settings.User is null)
                {
                    throw new SourceFailedException("Give a user name and password (Sign in with: User name and password), or choose another way to sign in.", permanent: true);
                }

                await CheckAsync($"the user name and password of {Settings.User}", ct).ConfigureAwait(false);
                User = Settings.User;
                How = "with a user name and password";
                break;

            case ArtifactorySignIn.Anonymous:
                await CheckAsync("a request with no sign-in: it lets no one read without one", ct).ConfigureAwait(false);
                How = "without a sign-in";
                break;

            default:
                try
                {
                    if (await KeptAsync(ct).ConfigureAwait(false))
                    {
                        How = "with the sign-in kept from before";
                        break;
                    }

                    await ClientSignInAsync(interactive, progress, ct).ConfigureAwait(false);
                }
                catch (SignInRequiredException ex)
                {
                    throw new SourceFailedException(ex.Message, ex.Permanent, ex);
                }
                catch (Exception ex) when (IsNoAnswer(ex, ct))
                {
                    throw new SourceFailedException($"Artifactory ({Url.Host}) did not answer: {ex.Message}", inner: ex);
                }

                await CheckAsync("the token its sign-in gave", ct).ConfigureAwait(false);
                break;
        }

        _signedIn = true;
        _logger.LogInformation("artifactory: signed in to {Host} as {User} {How}", Url.Host, Who, How);
        Save();
    }

    /// <summary>
    /// Asks Artifactory for its repositories with the sign-in: what every account may ask, anyone where the server lets anyone
    /// read, and none with a sign-in the server does not take.
    /// </summary>
    private async Task CheckAsync(string refused, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Address.Api("repositories"));
            await AuthorizeAsync(request, ct).ConfigureAwait(false);
            response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNoAnswer(ex, ct))
        {
            throw new SourceFailedException($"Artifactory ({Url.Host}) did not answer: {ex.Message}", inner: ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var said = ArtifactoryApi.Message(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                throw new SourceFailedException($"Artifactory ({Url.Host}) refused {refused}{(said is null ? "" : $" ({said})")}.", permanent: true);
            }

            if (!await TookRepositoriesAsync(response, ct).ConfigureAwait(false))
            {
                throw new SourceFailedException($"{Address.Base} did not answer as Artifactory does ({(int)response.StatusCode}): give the server's address as the browser shows it, like https://artifactory.example.com/artifactory.", permanent: (int)response.StatusCode is 404 or 200);
            }
        }
    }

    /// <summary>Keeps the repositories an answer to <c>api/repositories</c> lists for the first to ask (<see cref="TakeRepositories"/>); false for an answer that lists none.</summary>
    private async Task<bool> TookRepositoriesAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var doc = await JsonOfAsync(response, ct).ConfigureAwait(false);
        if (doc?.RootElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        // what a start asks for next, and the Test: kept for the first to ask (TakeRepositories)
        Volatile.Write(ref _checked, new Checked(ArtRepository.ListOf(doc.RootElement), Environment.TickCount64));
        return true;
    }

    // the repositories the sign-in's check read, and when, until someone takes them
    private sealed record Checked(List<ArtRepository> Repositories, long At);

    private Checked? _checked;

    /// <summary>
    /// The repositories the sign-in's check read (<c>api/repositories</c>, every one the account may see), for the first to
    /// ask for them within <paramref name="fresh"/> of then: a start, and the Test, ask nothing more for their first list.
    /// Null once taken, or past that time.
    /// </summary>
    public List<ArtRepository>? TakeRepositories(TimeSpan fresh)
        => Interlocked.Exchange(ref _checked, null) is { } taken && Environment.TickCount64 - taken.At <= fresh.TotalMilliseconds ? taken.Repositories : null;

    /// <summary>The account a signed token names (its subject, <c>…/users/name</c>); null for a token that says nothing (a reference token, an API key).</summary>
    internal static string? NameIn(string token)
        => Claims(token, c => c.Str("sub") is { } subject && subject.LastIndexOf("/users/", StringComparison.Ordinal) is var users and >= 0 && subject.Length > users + 7 ? subject[(users + 7)..] : null);

    /// <summary>When a signed token runs out (its <c>exp</c>); null for one that does not say, or does not run out.</summary>
    internal static DateTimeOffset? ExpiryOf(string token) => Claims(token, c => c.Long("exp") is > 0 and var exp ? DateTimeOffset.FromUnixTimeSeconds(exp) : (DateTimeOffset?)null);

    /// <summary>When a signed token was made (its <c>iat</c>); null for one that does not say.</summary>
    internal static DateTimeOffset? IssuedOf(string token) => Claims(token, c => c.Long("iat") is > 0 and var iat ? DateTimeOffset.FromUnixTimeSeconds(iat) : (DateTimeOffset?)null);

    /// <summary>What a signed token's claims say, read by <paramref name="read"/>; default for a token that is not one (a reference token, an API key).</summary>
    private static T? Claims<T>(string token, Func<JsonElement, T?> read)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return default;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.ValueKind == JsonValueKind.Object ? read(doc.RootElement) : default;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return default;
        }
    }

    /// <summary>An API key, which Artifactory reads from a header of its own, not as a bearer token: they begin <c>AKC</c>.</summary>
    internal static bool IsApiKey(string token) => token.StartsWith("AKC", StringComparison.Ordinal) && !token.Contains('.');

    /// <summary>
    /// The signed-in account as the platform's own pages learn it (<c>ui/api/v1/ui/auth/current</c>), for a sign-in that does
    /// not name it; null where the platform does not say (an Artifactory before the platform, a server that keeps the call
    /// to its pages), or says nobody.
    /// </summary>
    private async Task<string?> NameFromPlatformAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Platform, "ui/api/v1/ui/auth/current"));
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            await AuthorizeAsync(request, ct).ConfigureAwait(false);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            {
                return null;
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.Str("name") is { Length: > 0 } name && !name.Equals("anonymous", StringComparison.OrdinalIgnoreCase) ? name : null;
        }
        catch (Exception ex) when (ex is JsonException || IsNoAnswer(ex, ct))
        {
            return null;
        }
    }

    // --- the web session: JFrog's client sign-in -----------------------------------------------------------------------

    /// <summary>
    /// A token of a sign-in just made (the Test's) or kept from the last run, while Artifactory takes it: renewed first where
    /// it has run out, or Artifactory refuses it.
    /// </summary>
    private async Task<bool> KeptAsync(CancellationToken ct)
    {
        if (_standIn is { UseKept: false })
        {
            return false;
        }

        // what the dialog's Test just left for these settings first (this session's alone from now on), then what was kept
        // at the last run (KeptSignIn)
        foreach (var kept in _kept.Find(Address.Base, Settings.Who))
        {
            if (ArtifactoryClientToken.Parse(kept) is not { } token)
            {
                continue;
            }

            _client = token;
            var runOut = token.Until is { } until && until <= ServerNow;
            if (!runOut && await TakesTokenAsync(ct).ConfigureAwait(false) || await RefreshAsync(ct).ConfigureAwait(false) && await TakesTokenAsync(ct).ConfigureAwait(false))
            {
                User = _client.User ?? NameIn(_client.Access);
                return true;
            }
        }

        _logger.LogInformation("artifactory: no sign-in kept from before for {Host} that Artifactory still takes", Url.Host);
        _client = null;
        return false;
    }

    /// <summary>Whether Artifactory takes the web session's token: its repositories, asked with it (and kept for the start, as the sign-in's check keeps them).</summary>
    private async Task<bool> TakesTokenAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Address.Api("repositories"));
        await AuthorizeAsync(request, ct).ConfigureAwait(false);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        return await TookRepositoriesAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// JFrog's client sign-in: as a browser window of AnyFS's own opens for the person, a session of AnyFS's own asked of the
    /// platform and its sign-in page opened there (the person signs in and gives the code AnyFS shows them), then the token
    /// the platform hands over for the session. The platform holds the session for five minutes from when it was asked
    /// for, so it is asked for only once the window's turn has come (another sign-in of the server may have its window
    /// open), and the window closes when the five minutes are up.
    /// </summary>
    private async Task ClientSignInAsync(bool interactive, Action<string> progress, CancellationToken ct)
    {
        if (!interactive)
        {
            throw new SignInRequiredException($"the Artifactory sign-in for {Url.Host} has lapsed, and only you can sign in again (the sign-in page asks you for a code): open the source, or its Retry");
        }

        var target = new ArtifactorySignInTarget(Platform, AskForSessionAsync, SessionTokenAsync, _standIn?.SessionHeld);
        ArtifactoryClientToken? token;
        string browserName;
        if (_standIn?.Browser is { } person)
        {
            browserName = "the browser";
            token = await StandInWindowAsync(person, target, progress, ct).ConfigureAwait(false);
        }
        else
        {
            var signIn = new BrowserSession(BrowserLocator.Find(Settings.Browser, "ANYFS_ARTIFACTORY_BROWSER"), target, _logger, Settings.Browser);
            if (!signIn.CanSignIn)
            {
                throw new SignInRequiredException("No Edge, Chrome, Chromium or Firefox was found for the web session's sign-in. Install one, set the Browser path under Advanced, or sign in with an access token.", permanent: true);
            }

            await signIn.SignInAsync(interactive, progress, ct).ConfigureAwait(false);
            browserName = signIn.BrowserName;
            token = target.Token;
        }

        _client = token ?? throw new SignInRequiredException($"{browserName} signed in to {Url.Host}, but Artifactory gave AnyFS no token for the sign-in; Retry to sign in again");
        User = token.User ?? NameIn(token.Access);
        How = $"with {browserName}";
        _logger.LogInformation("artifactory: signed in to {Host} with {Browser} through JFrog's client sign-in; the token runs to {Until}", Url.Host, browserName, token.Until?.ToString("u") ?? "no end");
    }

    /// <summary>
    /// The stand-in's window (tests), kept as <see cref="BrowserSession"/> keeps the person's: one window at a time on the
    /// server's profile, the session asked for and the person told its code as it opens, then the target checked until it
    /// has the token or says the sign-in cannot end in one, which closes the window; a person who closes the window first
    /// (whose stand-in finishes) ends the sign-in. A stand-in that fails is a test's own failure, and is thrown as it is.
    /// </summary>
    private static async Task<ArtifactoryClientToken?> StandInWindowAsync(Func<bool, Uri, CancellationToken, Task> person, ArtifactorySignInTarget target, Action<string> progress, CancellationToken ct)
    {
        var turn = ProfileLocks.For(target.ProfileDir);
        await turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await target.OpeningWindowAsync(ct).ConfigureAwait(false);
            progress(target.Prompt("the browser"));
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var at = person(true, target.Page, window.Token);
            try
            {
                DateTime? closedSince = null;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (at.IsFaulted)
                    {
                        await at.ConfigureAwait(false);
                    }

                    // a window gone for three seconds is a closed one, as BrowserSession tells it; the target is checked
                    // meanwhile, for a code given just before
                    closedSince = at.IsCompleted ? closedSince ?? DateTime.UtcNow : null;
                    var check = await target.CheckAsync([], [], ct).ConfigureAwait(false);
                    if (check.Done)
                    {
                        return target.Token;
                    }

                    if (check.Failure is { } failure)
                    {
                        throw new SignInRequiredException(failure, permanent: true);
                    }

                    if (DateTime.UtcNow - closedSince > TimeSpan.FromSeconds(3))
                    {
                        throw new SignInRequiredException($"the browser window was closed before the sign-in to {target.Page.Host} finished; Retry to sign in", permanent: true);
                    }

                    var pace = Task.Delay(TimeSpan.FromMilliseconds(25), ct);
                    await (at.IsCompleted ? pace : Task.WhenAny(at, pace)).ConfigureAwait(false);
                }
            }
            finally
            {
                // the window closes, and the person's stand-in ends with it before the sign-in goes on, as BrowserSession
                // closes the browser before it returns
                await window.CancelAsync().ConfigureAwait(false);
                try
                {
                    await at.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException || at.IsFaulted)
                {
                    // its ending, or its own failure, which the loop has thrown already where it came first
                }
            }
        }
        finally
        {
            turn.Release();
        }
    }

    /// <summary>
    /// Asks the platform for a client sign-in under <paramref name="session"/>. A platform without one (an Artifactory older
    /// than 7.64, or no platform before it) cannot have a web session: an access token can sign in instead.
    /// </summary>
    private async Task AskForSessionAsync(string session, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Platform, "access/api/v2/authentication/jfrog_client_login/request"))
        {
            Content = new StringContent(new JsonObject { ["session"] = session }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var said = ArtifactoryApi.Message(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        _logger.LogInformation("artifactory: {Host} would not start a client sign-in: {Status} {Said}", Url.Host, (int)response.StatusCode, said);
        throw response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
            ? new SignInRequiredException($"Artifactory ({Url.Host}) has no client sign-in, which the web session signs in through (JFrog's web login, in Artifactory 7.64 and later): choose Access token under Sign in with, and give an identity token from your profile there", permanent: true)
            : new SignInRequiredException($"Artifactory ({Url.Host}) would not start a sign-in ({(int)response.StatusCode}{(said is null ? "" : ": " + said)})");
    }

    /// <summary>
    /// The token the platform holds for a client sign-in's session, once the person has signed in and given the code; null
    /// until then (the platform answers 400), and where it does not answer. The platform hands it over once.
    /// </summary>
    private async Task<ArtifactoryClientToken?> SessionTokenAsync(string session, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Platform, $"access/api/v2/authentication/jfrog_client_login/token/{Uri.EscapeDataString(session)}"));
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            using var doc = await JsonOfAsync(response, ct).ConfigureAwait(false);
            return doc is null ? null : ArtifactoryClientToken.From(doc.RootElement);
        }
        catch (Exception ex) when (IsNoAnswer(ex, ct))
        {
            return null;
        }
    }

    /// <summary>
    /// Renews the web session's token as JFrog's own tool does: the refresh token, which the platform takes once, with the
    /// access token it came with, as the bearer and beside it; the answer holds the next of each. False where the platform
    /// will not (the refresh token spent or revoked, the account gone): only the person can sign in again. A server that
    /// failed throws, as one that did not answer does: it has refused nothing.
    /// </summary>
    private async Task<bool> RefreshAsync(CancellationToken ct)
    {
        if (_client is not { Refresh: { Length: > 0 } refresh } token)
        {
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Platform, "access/api/v1/tokens"))
        {
            Content = new StringContent(new JsonObject { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["access_token"] = token.Access }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Access);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        using var doc = await JsonOfAsync(response, ct).ConfigureAwait(false);
        if (doc is not null && ArtifactoryClientToken.From(doc.RootElement) is { } renewed)
        {
            _client = renewed with { User = renewed.User ?? token.User };
            _logger.LogInformation("artifactory: renewed the sign-in for {Host}; its token runs to {Until}", Url.Host, _client.Until?.ToString("u") ?? "no end");

            // kept at once, signed in yet or not: the refresh token it replaces is spent. A kept token renewed as a start
            // begins was kept only once the start was through, two requests on, and a start cut short between left the
            // spent one in the store: the next start asked the person for a browser and a code (R9a)
            _kept.Keep(Address.Base, Settings.Who, _client.ToJson());
            return true;
        }

        // a server that failed (5xx) or holds requests back (429) has refused nothing: the renewal is tried again later
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException($"the renewal of the sign-in was answered {(int)response.StatusCode}", null, response.StatusCode);
        }

        _logger.LogInformation("artifactory: {Host} would not renew the sign-in ({Status})", Url.Host, (int)response.StatusCode);
        return false;
    }

    /// <summary>
    /// Whether the web session's token is near enough its end to be renewed before a request, as JFrog's own tool does it:
    /// when a tenth of its life is left, at least half an hour and at most a week before the end (and never more than half
    /// its life).
    /// </summary>
    private bool RenewalDue(ArtifactoryClientToken token)
    {
        if (token.Until is not { } until)
        {
            return false;
        }

        var life = token.Issued is { } issued && until > issued ? until - issued : TimeSpan.FromHours(1);
        var ahead = TimeSpan.FromTicks(Math.Min(Math.Clamp(life.Ticks / 10, TimeSpan.FromMinutes(30).Ticks, TimeSpan.FromDays(7).Ticks), life.Ticks / 2));
        return ServerNow >= until - ahead;
    }

    /// <summary>Adds what the sign-in needs to a request to the server itself; nothing to one sent on elsewhere (a file's address in a storage service).</summary>
    public async ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = null;
        request.Headers.Remove("X-JFrog-Art-Api");
        if (request.RequestUri is not { } url || !OnServer(url))
        {
            return;
        }

        switch (Method)
        {
            case ArtifactorySignIn.Token when Settings.Token is { } token:
                if (IsApiKey(token))
                {
                    request.Headers.TryAddWithoutValidation("X-JFrog-Art-Api", token);
                }
                else
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                break;
            case ArtifactorySignIn.Password when Settings.User is { } user:
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{Settings.Password}")));
                break;
            case ArtifactorySignIn.WebSession:
                // a token near its end is renewed first, as JFrog's own tool renews its own
                if (_signedIn && _lapsed is null && _client is { } due && RenewalDue(due))
                {
                    try
                    {
                        await RenewAsync(Generation, ct).ConfigureAwait(false);
                    }
                    catch (VfsException)
                    {
                        // the request goes as it is, and its refusal says why
                    }
                }

                if (_client is { } client)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", client.Access);
                }

                break;
        }
    }

    /// <summary>Whether an address is the server's own (its scheme and host, with the port), not another's it sent a request on to.</summary>
    public bool OnServer(Uri url)
        => url.Scheme.Equals(Address.Base.Scheme, StringComparison.OrdinalIgnoreCase) && url.Authority.Equals(Address.Base.Authority, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// After a request seen at <paramref name="seen"/> (its <see cref="Generation"/>) was refused, or as the web session's
    /// token nears its end: the token renewed with its refresh token, once for every request refused with it. Any other
    /// sign-in cannot be renewed; a renewal the platform refuses needs the person at the sign-in page again, and throws a
    /// <see cref="VfsException"/> saying so (the source shows Degraded; Retry signs in with a window).
    /// </summary>
    public async Task RenewAsync(int seen, CancellationToken ct)
    {
        if (!CanRenew)
        {
            throw new VfsException(VfsError.Unavailable, $"Artifactory ({Url.Host}) no longer takes {Settings.Who}: give a new one and Retry the source.");
        }

        if (!await _renewal.RenewAsync(seen, ct).ConfigureAwait(false))
        {
            throw new VfsException(VfsError.Unavailable, $"Artifactory ({Url.Host}) refused the sign-in again just after it was renewed; Retry the source to sign in again.");
        }
    }

    /// <summary>
    /// The token renewed with its refresh token, as <see cref="SignInRenewal"/> runs it: once for all that were refused,
    /// on the session's lifetime and no request's. A refresh token is good for one use: a renewal stopped with the
    /// request that began it, after the platform had taken the token, lost the pair it answered with.
    /// </summary>
    private async Task RefreshAgainAsync(CancellationToken ct)
    {
        // a renewal Artifactory has refused is not asked for again: only the person can sign in now
        if (_lapsed is { } lapsed)
        {
            throw new VfsException(VfsError.Unavailable, lapsed);
        }

        bool renewed;
        try
        {
            renewed = await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNoAnswer(ex, ct))
        {
            throw new VfsException(VfsError.Unavailable, $"Artifactory ({Url.Host}) did not answer: {ex.Message}", ex);
        }

        if (!renewed)
        {
            _lapsed = $"The Artifactory sign-in for {Url.Host} has lapsed, and only you can sign in again (the sign-in page asks you for a code): Retry the source to sign in.";
            throw new VfsException(VfsError.Unavailable, _lapsed);
        }
    }
    /// <summary>The sign-in was renewed: what failed for want of one may be asked again.</summary>
    public event Action? Renewed;

    /// <summary>
    /// Keeps a signed-in web session's token: a source's in its secret store for the next start; the Test's, which has no
    /// store, for the source started from the same dialog. A session that never signed in keeps nothing: a start that
    /// failed or was stopped before its sign-in holds no token, and keeping that would take the place of one Artifactory
    /// still takes.
    /// </summary>
    public void Save()
    {
        if (Method != ArtifactorySignIn.WebSession || !_signedIn || _client is not { } client)
        {
            return;
        }

        _kept.Keep(Address.Base, Settings.Who, client.ToJson());
    }

    public void Dispose()
    {
        Http.Dispose();
        _handler.Dispose();
        _renewal.Dispose();
    }

    /// <summary>Notes how far the server's clock is from this computer's, from each answer's Date (a second's grain, which is finer than what it is used for), and the server's version, which every answer of Artifactory's names.</summary>
    private sealed class ClockHandler(ArtifactorySession session, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            if (response.Headers.Date is { } date && request.RequestUri is { } url && session.OnServer(url))
            {
                Volatile.Write(ref session._clockAheadTicks, (date - DateTimeOffset.UtcNow).Ticks);
            }

            // "Artifactory/7.161.26 77161260"
            if (session.Version is null && response.Headers.TryGetValues("X-JFrog-Version", out var named) && named.FirstOrDefault() is { } product
                && product.StartsWith("Artifactory/", StringComparison.OrdinalIgnoreCase))
            {
                session.Version = product["Artifactory/".Length..].Split(' ')[0];
            }

            return response;
        }
    }
}
