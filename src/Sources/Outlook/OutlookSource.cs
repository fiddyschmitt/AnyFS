using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using AnyFS.Sdk;
using AnyFS.Sources.Outlook.Ews;
using AnyFS.Sources.Outlook.Files;
using AnyFS.Sources.Outlook.Mail;
using LibOutlook;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Outlook;

/// <summary>
/// An Outlook source (spec 7.7): a mailbox on Exchange Online through the web session or an app sign-in, a mailbox on
/// an Exchange server, or a PST or OST file; either way a mail store (<see cref="IMailStore"/>) whose folders and item
/// names make the tree (<see cref="MailTree"/>), kept in memory, and whose items are opened when their folder is, each
/// item a folder with the item as Markdown and .eml (or .ics, or a .vcf for a contact) and its attachments. An item is
/// opened with its neighbours, a mailbox's in one request (<see cref="PrepareAsync"/>), and its folder is listed after
/// that from what its files came to (<see cref="ItemListings"/>: names, lengths, dates), kept between starts while the
/// item is as it was. Nothing of a message is written to disk. Every <see cref="OutlookSettings.RefreshMinutes"/> the
/// store is asked what changed (a file: when it changed on disk; a mailbox: each folder's delta) and the tree swapped
/// whole. Trouble upstream shows as Degraded until a read succeeds.
/// </summary>
public sealed class OutlookSource : SourceBase, ICountable
{
    private static readonly TimeSpan ItemsKeptFor = TimeSpan.FromMinutes(3);
    private const int ItemsKept = 512;

    /// <summary>What the items' files kept in memory may come to, by their lengths (256 MB): the oldest go first.</summary>
    private const long ItemsKeptBytes = 256L << 20;

    /// <summary>Items opened together at most (<see cref="PrepareAsync"/>).</summary>
    internal const int BatchItems = 16;

    /// <summary>What items opened together may come to at most, by the sizes their folders list (8 MB): one bigger goes alone.</summary>
    private const long BatchBytes = 8L << 20;

    /// <summary>How often the items' listings are kept for the next start at most, while they change (and at every refresh and stop).</summary>
    private static readonly TimeSpan ListingsKeptEvery = TimeSpan.FromMinutes(1);

    private readonly OutlookSettings _settings;
    private readonly OutlookStandIn? _standIn;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ConcurrentDictionary<string, KeptFiles> _items = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<ExceptionDispatchInfo?>> _pending = new(StringComparer.Ordinal);
    private readonly ItemListings _listings = new();
    private readonly SemaphoreSlim _listingsGate = new(1, 1);
    private readonly Lock _trimGate = new();
    private readonly SemaphoreSlim _listAllGate = new(1, 1);
    private readonly Lock _countGate = new();
    private long _itemsBytes;
    private long _listingsKept;
    private DateTime _listingsKeptAt = DateTime.UtcNow;

    /// <summary>The tree <see cref="Count"/> counts, with what its listed items' files add to its numbers and how many of its items are not listed.</summary>
    private MailTree _countedTree = MailTree.Empty;
    private long _extraFiles;
    private long _extraFolders;
    private long _unlisted;

    /// <summary>Whether "Count files on start" asked for every item's listing: then items new since are listed as refreshes find them.</summary>
    private volatile bool _listingAll;

    /// <summary>How many listings of every item are under way: the count is then on its way (InProgress), not only so far (Unknown).</summary>
    private int _listing;
    private volatile MailTree _tree = MailTree.Empty;
    private IMailStore? _store;
    private AddressBooks? _books;
    private IDisposable? _directoryOwned;
    private Task _refreshing = Task.CompletedTask;
    private (long Length, DateTime Written) _seen;
    private bool _degraded;

    /// <summary>What encrypted S/MIME mail is opened with (the user's certificates; for tests, <see cref="SmimeCertificates"/> too).</summary>
    private SmimeKeys? _keys;

    /// <summary>The mailbox store's revision its listing was last kept at (<see cref="KeepListingAsync"/>).</summary>
    private long _keptRevision = -1;

    public OutlookSource(OutlookSettings settings)
        : this(settings, null)
    {
    }

    /// <param name="standIn">What a test puts in place of Exchange and the browser; null for the real thing.</param>
    internal OutlookSource(OutlookSettings settings, OutlookStandIn? standIn)
    {
        _settings = settings;
        _standIn = standIn;
    }

    public override SourceCapabilities Capabilities => new(CanWrite: false, CaseInsensitive: true);

    /// <summary>Certificates (with their private keys) that open encrypted S/MIME mail as well as the user's (for tests).</summary>
    internal System.Security.Cryptography.X509Certificates.X509Certificate2Collection? SmimeCertificates { get; init; }

    private SmimeKeys _smimeKeys => _keys ??= new SmimeKeys(SmimeCertificates);

    /// <summary>How long an address list that answered nothing waits to be asked once more (for tests: none).</summary>
    internal TimeSpan DirectoryColdPause { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The tree served now (for tests).</summary>
    internal MailTree Tree => _tree;

    /// <summary>The store (for tests).</summary>
    internal IMailStore Store => _store ?? throw new InvalidOperationException("the source has not started");

    private TimeSpan RefreshEvery => TimeSpan.FromMinutes(Math.Max(0, _settings.RefreshMinutes));

    protected override async Task OnInitialiseAsync(CancellationToken ct)
    {
        var file = _settings.File ?? throw new SourceFailedException("Give a mailbox's address, or the path of a .pst or .ost file.", permanent: true);
        switch (_settings.Kind)
        {
            case OutlookKind.DataFile:
                if (!File.Exists(file))
                {
                    throw new SourceFailedException($"No file at {file}.", permanent: false);
                }

                Context.ReportProgress($"Reading {Path.GetFileName(file)}");
                try
                {
                    _store = await Task.Run(() => new PstMailStore(file, _settings.ShowHidden, Logger, _settings.CalendarMonthsAhead), ct).ConfigureAwait(false);
                    _seen = Stamp(file);
                }
                catch (PstException ex)
                {
                    throw new SourceFailedException($"{Path.GetFileName(file)} could not be read as an Outlook data file: {ex.Message}", permanent: true, ex);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new SourceFailedException($"{file} could not be opened: {ex.Message}", permanent: false, ex);
                }

                break;
            case OutlookKind.Mailbox:
            case OutlookKind.Server:
                {
                    if (_settings.Kind == OutlookKind.Server && _settings.IsWindowsCurrent && !OperatingSystem.IsWindows())
                    {
                        throw new SourceFailedException("Signing in as yourself works on Windows only: choose Windows account and give a user name and password.", permanent: true);
                    }

                    var secrets = Context.Secrets;
                    var hooks = new SignInHooks(
                        () => secrets.Get("msal-cache") is { } saved ? Convert.FromBase64String(saved) : null,
                        bytes => secrets.Set("msal-cache", Convert.ToBase64String(bytes)),
                        (title, message) => Context.PromptAsync(new PromptRequest(PromptKind.YesNo, title, message), ct));
                    var (service, directory, account, how) = await ConnectAsync(_settings, _standIn, Logger, interactive: true, message => Context.ReportProgress(message), hooks, ct).ConfigureAwait(false);
                    _store = new EwsMailStore(service, _settings.Kind == OutlookKind.Server ? $"{account} on {_settings.ServerUrl!.Host}" : account, _settings.ShowHidden, _settings.CalendarMonthsBack, _settings.CalendarMonthsAhead, Logger);
                    if (directory is not null)
                    {
                        // the organisation's address lists, where the service reaches them (the web session's People, a server's NSPI)
                        _books = new AddressBooks(directory, _store.Name, Path.Combine(Context.StateDirectory, AddressBookIndexFile.FileName), Logger, DirectoryColdPause, _stopping.Token);
                        _books.Updated += Count;

                        // the web session's directory is its mail service (disposed with the store); a server's is its own
                        _directoryOwned = ReferenceEquals(directory, service) ? null : directory as IDisposable;
                    }

                    Logger.LogInformation("outlook: signed in as {Account} {How}", account, how);
                    break;
                }

            default:
                throw new SourceFailedException($"{file} is neither a mailbox's address, nor a server's, nor an Outlook data file (one ends in .pst or .ost).", permanent: true);
        }

        // each item's listing as last made: a folder the item is still as it was in is served without asking for the item
        _listings.Load(ListingsPath, StoreKey);

        // a mailbox listed before starts from that listing and asks for what changed since in the background
        var kept = _store is EwsMailStore mailbox ? MailboxIndexFile.Load(IndexPath, mailbox.Name, IndexShape) : null;
        if (kept is not null)
        {
            ((EwsMailStore)_store).Import(kept);
        }

        try
        {
            if (kept is null)
            {
                await _store.RefreshAsync(ct).ConfigureAwait(false);
            }

            if (_books is not null)
            {
                Context.ReportProgress("Reading the address lists");
                await _books.StartAsync(ct).ConfigureAwait(false);
            }

            Swap(await MailTree.BuildAsync(_store, ct, (done, total, name) => Context.ReportProgress($"Listing {name} ({done} of {total} folders)", total == 0 ? null : (double)done / total), _books?.Node, _settings.ShowHidden).ConfigureAwait(false));
            if (kept is null)
            {
                await KeepListingAsync(ct).ConfigureAwait(false);
            }
            else
            {
                _keptRevision = ((EwsMailStore)_store).Revision;
            }
        }
        catch (SignInRequiredException ex)
        {
            throw new SourceFailedException(ex.Message, permanent: ex.Permanent, ex);
        }
        catch (EwsException ex)
        {
            // a Windows sign-in the server refuses is the settings' fault, as is an app Graph will not let read the mailbox; the rest may pass
            throw new SourceFailedException($"Listing the mailbox: {ex.Message}", permanent: ex.Code == "401" && _settings.Kind == OutlookKind.Server || ex.Code == "ErrorAccessDenied" && _settings.IsAppSignIn, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SourceFailedException($"Listing the mailbox: {ex.Message}", permanent: false, ex);
        }
        catch (PstException ex)
        {
            throw new SourceFailedException($"{Path.GetFileName(file)} could not be read as an Outlook data file: {ex.Message}", permanent: true, ex);
        }

        if (_store is PstMailStore { Damaged: > 0 } damaged)
        {
            Logger.LogWarning("outlook: {Count:N0} places of {File} do not read in full (each named above); the rest of the file is served", damaged.Damaged, Path.GetFileName(file));
        }

        if (RefreshEvery > TimeSpan.Zero || kept is not null)
        {
            var stopping = _stopping.Token;
            _refreshing = Task.Run(() => RefreshLoopAsync(refreshNow: kept is not null, stopping), CancellationToken.None);
        }

        if (kept is not null)
        {
            Logger.LogInformation("outlook: serving {Name} as listed {Minutes:F0} minutes ago: {Folders:N0} folders, {Items:N0} items; asking what changed since, in the background",
                _store.Name, (DateTimeOffset.UtcNow - kept.Taken).TotalMinutes, _tree.FolderCount, _tree.ItemCount);
        }
        else
        {
            Logger.LogInformation("outlook: serving {Name}: {Folders:N0} folders, {Items:N0} items", _store.Name, _tree.FolderCount, _tree.ItemCount);
        }
    }

    /// <summary>Where a mailbox's listing is kept between starts.</summary>
    private string IndexPath => Path.Combine(Context.StateDirectory, MailboxIndexFile.FileName);

    /// <summary>Where the items' listings are kept between starts.</summary>
    private string ListingsPath => Path.Combine(Context.StateDirectory, ItemListings.FileName);

    /// <summary>Whose items the kept listings are of: the mailbox, or the data file by its full path.</summary>
    private string StoreKey => _settings.Kind == OutlookKind.DataFile ? Path.GetFullPath(_settings.File!) : Store.Name;

    /// <summary>
    /// Keeps the items' listings for the next start when they changed since they were last kept, those of items no longer
    /// listed left out: at once with <paramref name="now"/>, else at most every <see cref="ListingsKeptEvery"/>. With
    /// <paramref name="wait"/> it waits for a keeping under way (the stop), else it leaves it to that one. Not before the
    /// tree is built, which says which items are still there; a file that cannot be written is said and left.
    /// </summary>
    private async Task KeepListingsAsync(bool now, bool wait = false)
    {
        if (_listings.Changes == Interlocked.Read(ref _listingsKept) || !now && DateTime.UtcNow - _listingsKeptAt < ListingsKeptEvery || ReferenceEquals(_tree, MailTree.Empty))
        {
            return;
        }

        if (wait)
        {
            await _listingsGate.WaitAsync().ConfigureAwait(false);
        }
        else if (!await _listingsGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var changes = _listings.Changes;
            var tree = _tree;
            var kept = await Task.Run(() => _listings.Save(ListingsPath, StoreKey, tree.ById.ContainsKey)).ConfigureAwait(false);
            Interlocked.Exchange(ref _listingsKept, changes);
            _listingsKeptAt = DateTime.UtcNow;
            Logger.LogDebug("outlook: kept the listings of {Count:N0} items for the next start", kept);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning("outlook: the items' listings could not be kept for the next start: {Error}", ex.Message);
        }
        finally
        {
            _listingsGate.Release();
        }
    }

    /// <summary>The settings a mailbox's listing depends on: one kept under others is not used.</summary>
    private string IndexShape => FormattableString.Invariant($"{_settings.Kind}|hidden={_settings.ShowHidden}|back={_settings.CalendarMonthsBack}|ahead={_settings.CalendarMonthsAhead}");

    /// <summary>
    /// Keeps a mailbox's listing for the next start when it changed since it was last kept (spec 7.7: the listing index
    /// in the state folder, subjects and dates only, never a message). A file that cannot be written is said and left.
    /// </summary>
    private async Task KeepListingAsync(CancellationToken ct)
    {
        if (_store is not EwsMailStore mailbox || mailbox.Revision == _keptRevision)
        {
            return;
        }

        var revision = mailbox.Revision;
        var index = await mailbox.ExportAsync(IndexShape, ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => MailboxIndexFile.Save(IndexPath, index), ct).ConfigureAwait(false);
            _keptRevision = revision;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning("outlook: the listing of {Name} could not be kept for the next start: {Error}", mailbox.Name, ex.Message);
        }
    }

    /// <summary>What an app sign-in needs from where it runs: the saved sign-in (the source's secret store) and a way to show a device code; the Test button has neither.</summary>
    internal sealed record SignInHooks(Func<byte[]?> LoadCache, Action<byte[]> SaveCache, Func<string, string, Task>? Prompt);

    /// <summary>
    /// The EWS service for a mailbox, signed in: through the test's stand-in, the web session's token read from the
    /// browser (a window when <paramref name="interactive"/> and the saved sign-in has lapsed), an app sign-in and
    /// Microsoft Graph, or a Windows account to a server. Also who the account is and how it signed in, for messages.
    /// </summary>
    internal static async Task<(IEwsService Service, IAddressBookService? Directory, string Account, string How)> ConnectAsync(OutlookSettings s, OutlookStandIn? standIn, ILogger logger, bool interactive, Action<string> progress, SignInHooks? hooks, CancellationToken ct)
    {
        if (s.Kind == OutlookKind.Server)
        {
            // an Exchange server: SOAP with a Windows sign-in, the person's own mailbox there
            var url = s.EwsUrl!;
            var who = WindowsAccount.Name(s.IsWindowsCurrent, s.EffectiveSignIn == OutlookSignIn.Windows ? s.Username : null);
            var endpoint = standIn is null ? url : new Uri(standIn.Origin, url.PathAndQuery);
            var soap = new SoapEwsService(endpoint, WindowsAccount.Credentials(s.IsWindowsCurrent, s.Username, s.Password), s.TrustAnyCertificate, standIn?.Handler(), $"{url.Host}|{who}", logger);
            progress($"Signing in to {url.Host} as {who}");
            // the organisation's address books over NSPI, where the real server is reached (a test's stand-in does not speak it)
            var directory = standIn is null ? await ServerAddressBookAsync(s, soap, url.Host, who, logger, ct).ConfigureAwait(false) : null;
            return (soap, directory, standIn?.Account ?? who, $"with a Windows account ({url.Host})");
        }

        // a mailbox by its address, or by Outlook on the web's own (https://outlook.office365.us): then it is the one
        // signed in to there, known once the web session's token is read
        var address = s.Address;
        if (address is null && s.WebCloud is null)
        {
            throw new SourceFailedException("The mailbox's address is missing.", permanent: true);
        }

        if (s.IsAppSignIn)
        {
            if (address is null)
            {
                throw new SourceFailedException($"An app sign-in needs the mailbox's address in place of {s.WebHost}; with Outlook on the web's address, sign in with the web session.", permanent: true);
            }

            return await ConnectGraphAsync(s, address, standIn, logger, progress, hooks, ct).ConfigureAwait(false);
        }

        if (standIn is not null)
        {
            var service = new OwaJsonService(standIn.Origin, address ?? standIn.Account, standIn.Token, standIn.Handler(), logger);
            return (service, service, standIn.Account, "through the web session (a stand-in)");
        }

        var browser = OutlookPlugin.BrowserFor(s) ?? throw new SourceFailedException("The web session needs Microsoft Edge, Google Chrome or Chromium installed, or the path of one under Browser.", permanent: true);
        MicrosoftCloud cloud;
        if (s.WebCloud is { } named)
        {
            cloud = named;
        }
        else
        {
            progress($"Finding {address}'s cloud");
            cloud = MicrosoftCloud.FromName(s.Cloud) ?? await MicrosoftCloud.FindAsync(address!, null, ct).ConfigureAwait(false);
        }

        // a browser profile of its own per mailbox address; for Outlook on the web's address, one per cloud
        var origin = cloud.OutlookOrigin;
        var session = new OwaTokenSession(origin, browser, OutlookPlugin.ProfileDir(address ?? cloud.OutlookHost), logger);
        progress($"Reading the sign-in to {origin.Host} from {session.BrowserName}");
        var first = await session.TokenAsync(interactive, progress, ct).ConfigureAwait(false);
        var account = session.Account ?? address ?? cloud.OutlookHost;
        if (address is not null && !string.Equals(account, address, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("outlook: the browser profile for {Address} is signed in as {Account}", address, account);
        }

        var live = new OwaJsonService(origin, address ?? account.ToLowerInvariant(), async (renew, token) =>
        {
            if (renew)
            {
                session.Invalidate();
            }

            // a renewal shows no window: the source turns the lapse into Degraded and waits for a Retry
            return await session.TokenAsync(interactive: false, _ => { }, token).ConfigureAwait(false);
        }, null, logger);
        _ = first;
        return (live, live, account, $"through the web session ({session.BrowserName}, {origin.Host})");
    }

    /// <summary>
    /// Exchange Online after an app sign-in (spec 7.7.1): the cloud from the settings or the realm lookup, AnyFS's app or
    /// the organisation's own signed in to there (silently from the saved sign-in when it can, else the browser or a
    /// device code), then Microsoft Graph at the mailbox's pace.
    /// </summary>
    private static async Task<(IEwsService Service, IAddressBookService? Directory, string Account, string How)> ConnectGraphAsync(OutlookSettings s, string address, OutlookStandIn? standIn, ILogger logger, Action<string> progress, SignInHooks? hooks, CancellationToken ct)
    {
        var cloud = s.KnownCloud;
        if (cloud is null)
        {
            progress($"Finding {address}'s cloud");
            cloud = standIn is null ? await MicrosoftCloud.FindAsync(address, null, ct).ConfigureAwait(false) : MicrosoftCloud.Global;
        }
        if (!Guid.TryParse(s.EffectiveClientId, out _))
        {
            throw new SourceFailedException("The client ID is not a GUID.", permanent: true);
        }

        // the settings dialog checks this too, but not for a cloud only the lookup tells, nor for a source edited by hand
        if (CloudSettings.NeedsOwnRegistration(cloud, s.EffectiveClientId))
        {
            throw new SourceFailedException(CloudSettings.OwnRegistrationNeeded(cloud, OutlookPlugin.MailPermissions), permanent: true);
        }

        AppSignIn signIn;
        if (standIn is not null)
        {
            signIn = new AppSignIn(standIn.Account, (c, renew) => standIn.Token(renew, c));
        }
        else
        {
            var tenant = s.TenantIn(cloud);
            var entra = new EntraSignIn(new SignInOptions(OutlookPlugin.Service, s.EffectiveClientId, tenant, address, s.Method, OutlookPlugin.MailScopes, progress,
                hooks?.Prompt, hooks?.LoadCache ?? (() => EntraSignIn.RecentCache(s.EffectiveClientId, tenant, address)), hooks?.SaveCache ?? (_ => { }), Cloud: cloud), logger);
            progress(cloud.IsGlobal ? "Signing in to Exchange Online" : $"Signing in to Exchange Online in the {cloud.Name} cloud");
            try
            {
                await entra.SignInAsync(ct).ConfigureAwait(false);
            }
            catch (SignInRequiredException ex)
            {
                throw new SourceFailedException(ex.Message, permanent: ex.Permanent, ex);
            }

            signIn = new AppSignIn(entra.Username, entra.GetAccessTokenAsync);
        }

        var account = signIn.Username ?? address;
        if (!string.Equals(account, address, StringComparison.OrdinalIgnoreCase))
        {
            // Graph reads the signed-in account's own mailbox (me/), whatever the address says
            logger.LogWarning("outlook: the app sign-in for {Address} is {Account}, whose mailbox is the one read", address, account);
        }

        var graph = new GraphClient(signIn.Token, logger, OutlookPlugin.Service, handler: standIn?.Handler(), cloud: cloud, pace: RequestPace.For(GraphEwsService.PaceKey(cloud, address)));
        return (new GraphEwsService(graph, address, s.ShowHidden, logger), null, account, cloud.IsGlobal ? "with an app sign-in (Microsoft Graph)" : $"with an app sign-in (Microsoft Graph, {cloud.Name})");
    }

    /// <summary>
    /// A server's organisation address books over NSPI (spec 7.7; Spike 16): Autodiscover of the mapiHttp address book
    /// endpoint for the signed-in mailbox, reached with the same Windows sign-in as EWS. Null when Autodiscover offers no
    /// such endpoint (an older server, or MAPI over HTTP turned off) or anything goes wrong; the mailbox is served regardless.
    /// </summary>
    private static async Task<IAddressBookService?> ServerAddressBookAsync(OutlookSettings s, SoapEwsService soap, string host, string who, ILogger logger, CancellationToken ct)
    {
        var handler = new HttpClientHandler
        {
            Credentials = WindowsAccount.Credentials(s.IsWindowsCurrent, s.Username, s.Password),
            PreAuthenticate = true,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (s.TrustAnyCertificate)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AnyFS");
        try
        {
            // Autodiscover asks by the mailbox's SMTP address: resolved from the login name, with the login name and the host's own domain as fallbacks
            var login = s.EffectiveSignIn == OutlookSignIn.Windows && !string.IsNullOrWhiteSpace(s.Username) ? s.Username!.Trim() : Environment.UserName;
            var backslash = (char)92; // DOMAIN\name: keep only the name
            var alias = login.Contains(backslash) ? login[(login.LastIndexOf(backslash) + 1)..] : login;
            var addresses = new List<string>();
            try
            {
                addresses.AddRange(await soap.ResolveAddressesAsync(alias, ct).ConfigureAwait(false));
            }
            catch (EwsException ex) when (ex.Code == "401")
            {
                // the server refuses the sign-in itself: the mailbox's listing says so, and there is no address book to look for
                http.Dispose();
                return null;
            }
            catch (EwsException ex)
            {
                logger.LogInformation("outlook: {Host} could not resolve {Alias}'s address ({Error}); trying the login name and the host's domain for the address book", host, alias, ex.Message);
            }

            if (login.Contains('@'))
            {
                addresses.Add(login);
            }

            if (!IPAddress.TryParse(host, out _) && host.IndexOf('.') is > 0 and var dot)
            {
                addresses.Add($"{alias}@{host[(dot + 1)..]}");
                addresses.Add($"{alias}@{host}");
            }

            var book = await ExchangeAutodiscover.AddressBookUrlAsync(http, s.ServerUrl!, addresses, logger, ct).ConfigureAwait(false);
            if (book is null)
            {
                logger.LogInformation("outlook: {Host} offers no MAPI over HTTP address book; the Address Books folder is left out", host);
                http.Dispose();
                return null;
            }

            logger.LogInformation("outlook: {Host}'s address book is served over NSPI", host);
            return new NspiAddressBook(book, http, MailboxPace.For($"{s.EwsUrl!.Host}|{who}"), logger);
        }
        catch (Exception ex) when (ex is HttpRequestException or SignInRequiredException or EwsException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("outlook: {Host}'s address book could not be set up; the Address Books folder is left out: {Error}", host, ex.Message);
            http.Dispose();
            return null;
        }
    }

    // --- refresh ----------------------------------------------------------------------------------------------------------

    private static (long, DateTime) Stamp(string file)
    {
        var info = new FileInfo(file);
        return (info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>Asks the store what changed and serves the new tree; a file is read again only when it changed on disk.</summary>
    internal async Task RefreshAsync(CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_settings.Kind == OutlookKind.DataFile)
            {
                var file = _settings.File!;
                if (!File.Exists(file))
                {
                    MarkDegraded($"{file} is gone; the last listing is still served");
                    return;
                }

                var stamp = Stamp(file);
                if (stamp == _seen)
                {
                    return;
                }

                _seen = stamp;
            }

            var started = Stopwatch.GetTimestamp();
            await Store.RefreshAsync(ct).ConfigureAwait(false);
            if (_books is not null)
            {
                await _books.RefreshAsync(ct).ConfigureAwait(false);
            }

            Swap(await MailTree.BuildAsync(Store, ct, null, _books?.Node, _settings.ShowHidden).ConfigureAwait(false));
            ForgetFiles();
            ListNewBehind();
            Logger.LogInformation("outlook: {Name} read again in {Seconds:F1} s: {Folders:N0} folders, {Items:N0} items", Store.Name, Stopwatch.GetElapsedTime(started).TotalSeconds, _tree.FolderCount, _tree.ItemCount);
            await KeepListingAsync(ct).ConfigureAwait(false);
            await KeepListingsAsync(now: true).ConfigureAwait(false);
            MarkHealthy();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <param name="refreshNow">Whether to ask for what changed at once (a mailbox served from its kept listing), and then every <see cref="RefreshEvery"/> when that is set.</param>
    private async Task RefreshLoopAsync(bool refreshNow, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!refreshNow)
                {
                    if (RefreshEvery <= TimeSpan.Zero)
                    {
                        break;
                    }

                    await Task.Delay(RefreshEvery, ct).ConfigureAwait(false);
                }

                refreshNow = false;
                await RefreshAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("outlook: looking for changes failed; the last listing is still served: {Error}", ex.Message);
                MarkDegraded(ex is SignInRequiredException ? ex.Message : $"looking for changes failed: {ex.Message}");
            }
        }
    }

    private void Swap(MailTree tree)
    {
        _tree = tree;
        Recount(tree);
        Count();
    }

    /// <summary>
    /// The counts: the tree's, with an item's files as its listing has them (its attachments too) once it has been listed,
    /// and the address books' entries known. Exact once every item has been listed (a message's attachments are known
    /// only once it is opened) and every address list read (its entries are read when it is first opened); until then a
    /// lower bound ("1,204+"), as a lazy source's, and "Count files on start" lists and reads the rest
    /// (<see cref="CountFilesAsync"/>), meanwhile on its way (InProgress).
    /// </summary>
    private void Count()
    {
        var books = _books;
        long files;
        long folders;
        bool listed;
        lock (_countGate)
        {
            files = _countedTree.FileCount + _extraFiles;
            folders = _countedTree.FolderCount + _extraFolders;
            listed = _unlisted == 0;
        }

        var state = listed && (books is null || books.EntriesKnown) ? EnumerationState.Complete
            : Volatile.Read(ref _listing) > 0 ? EnumerationState.InProgress : EnumerationState.Unknown;
        Counters.Set(files + (books?.FileCount ?? 0), folders + (books?.FolderCount ?? 0), state);
    }

    /// <summary>Counts a new tree's items against their listings, afresh: what the listed ones add, how many are not listed.</summary>
    private void Recount(MailTree tree)
    {
        lock (_countGate)
        {
            long files = 0;
            long folders = 0;
            long unlisted = 0;
            foreach (var node in tree.ById.Values)
            {
                if (node is not ItemNode item)
                {
                    continue;
                }

                if (_listings.For(item.Item) is { } listing)
                {
                    files += listing.FileCount - ItemFiles.CountOf(item.Item);
                    folders += listing.HasAttachmentsFolder ? 1 : 0;
                }
                else
                {
                    unlisted++;
                }
            }

            _countedTree = tree;
            _extraFiles = files;
            _extraFolders = folders;
            _unlisted = unlisted;
        }
    }

    /// <summary>Keeps an item's listing (<paramref name="lasting"/>: for the next start too) and counts what that changes, for an item of the tree as it lists it.</summary>
    private void Listed(MailItemSummary item, ItemListing listing, bool lasting)
    {
        lock (_countGate)
        {
            var before = _listings.For(item);
            if (!_listings.Keep(item, listing, lasting))
            {
                return;
            }

            if (_countedTree.ById.GetValueOrDefault(item.Id) is ItemNode { Item: var counted } && ItemListings.StampOf(counted) == ItemListings.StampOf(item))
            {
                _extraFiles += listing.FileCount - (before?.FileCount ?? ItemFiles.CountOf(item));
                _extraFolders += (listing.HasAttachmentsFolder ? 1 : 0) - (before?.HasAttachmentsFolder == true ? 1 : 0);
                if (before is null)
                {
                    _unlisted--;
                }
            }
        }

        Count();
    }

    /// <summary>
    /// "Count files on start" (spec 6.8): the address lists not read yet are read, then every item not listed yet is
    /// listed (<see cref="ListAllAsync"/>), so the count takes in every attachment and is exact; and from then on, items new
    /// since are listed as the refreshes find them. The host's walk would have done the same an item at a time.
    /// </summary>
    public async Task CountFilesAsync(CancellationToken ct)
    {
        _listingAll = true;
        Interlocked.Increment(ref _listing);
        try
        {
            if (_books is { } books)
            {
                await books.ReadUnreadAsync(ct).ConfigureAwait(false);
            }

            await ListAllAsync(report: true, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _listing);
            Count();
        }
    }

    /// <summary>After a refresh, when "Count files on start" asked for every item's listing: the items new since, listed behind.</summary>
    private void ListNewBehind()
    {
        if (!_listingAll)
        {
            return;
        }

        var stopping = _stopping.Token;
        _ = Task.Run(async () =>
        {
            Interlocked.Increment(ref _listing);
            try
            {
                await ListAllAsync(report: false, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            finally
            {
                Interlocked.Decrement(ref _listing);
                Count();
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Lists every item not listed yet: a folder's items a batch at a time (<see cref="BatchItems"/>), two batches at once
    /// (of the four requests a mailbox takes at a time, two are left to whoever browses), their files not kept; the count
    /// grows as it goes. An item that does not list is left to its folder's opening, which says why; when the store fails
    /// as a whole (the network, the sign-in, a server too busy) the listing waits, a minute and then longer, and goes on.
    /// Items found meanwhile (a refresh) are listed before it ends. With <paramref name="report"/> (the host's count), how
    /// far it got shows on the tile. One at a time.
    /// </summary>
    private async Task ListAllAsync(bool report, CancellationToken ct)
    {
        await _listAllGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tried = new HashSet<string>(StringComparer.Ordinal);
            var pause = TimeSpan.FromMinutes(1);
            var started = Stopwatch.GetTimestamp();
            var done = 0;
            while (true)
            {
                var todo = _tree.ById.Values.Where(n => !tried.Contains(n.Item!.Id) && _listings.For(n.Item!) is null).ToList();
                if (todo.Count == 0)
                {
                    break;
                }

                var total = done + todo.Count;
                if (done == 0)
                {
                    Logger.LogInformation("outlook: listing {Count:N0} items of {Name} for the count", todo.Count, Store.Name);
                }

                var failure = await PassAsync(todo, tried, listed =>
                {
                    var now = Interlocked.Add(ref done, listed);
                    if (report)
                    {
                        Context.ReportProgress($"Counting files: {now:N0} of {total:N0} items", (double)now / total);
                    }
                }, ct).ConfigureAwait(false);
                if (failure is null)
                {
                    continue;
                }

                Logger.LogWarning("outlook: listing the items for the count waits {Minutes:F0} minutes for the mailbox: {Error}", pause.TotalMinutes, failure.SourceException.Message);
                await Task.Delay(pause, ct).ConfigureAwait(false);
                pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, TimeSpan.FromMinutes(15).Ticks));
            }

            if (done > 0)
            {
                Logger.LogInformation("outlook: listed {Count:N0} items of {Name} in {Seconds:F0} s", done, Store.Name, Stopwatch.GetElapsedTime(started).TotalSeconds);
            }
        }
        finally
        {
            _listAllGate.Release();
            await KeepListingsAsync(now: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One pass of <see cref="ListAllAsync"/> over <paramref name="todo"/>: each folder's items in batches, two at once;
    /// tells <paramref name="listed"/> how many each batch took and marks them <paramref name="tried"/>. Stops at a failure
    /// of the store as a whole and returns it (that batch's items left untried); null when it went through.
    /// </summary>
    private async Task<ExceptionDispatchInfo?> PassAsync(List<TreeNode> todo, HashSet<string> tried, Action<int> listed, CancellationToken ct)
    {
        using var slots = new SemaphoreSlim(2, 2);
        var running = new List<Task>();
        ExceptionDispatchInfo? stopped = null;
        try
        {
            foreach (var folder in todo.GroupBy(n => n.Parent))
            {
                var batch = new List<(TreeNode Node, TaskCompletionSource<ExceptionDispatchInfo?> Done)>();
                long bytes = 0;
                foreach (var node in folder)
                {
                    // one listed meanwhile, or being made for whoever browses, is left to that
                    var item = node.Item!;
                    if (Ready(item, forFiles: false))
                    {
                        continue;
                    }

                    if (batch.Count > 0 && (batch.Count == BatchItems || bytes + item.Size > BatchBytes))
                    {
                        if (!await StartAsync(batch).ConfigureAwait(false))
                        {
                            return await EndedAsync().ConfigureAwait(false);
                        }

                        batch = [];
                        bytes = 0;
                    }

                    var waiter = new TaskCompletionSource<ExceptionDispatchInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (_pending.TryAdd(item.Id, waiter.Task))
                    {
                        batch.Add((node, waiter));
                        bytes += item.Size;
                    }
                }

                if (batch.Count > 0 && !await StartAsync(batch).ConfigureAwait(false))
                {
                    return await EndedAsync().ConfigureAwait(false);
                }
            }

            return await EndedAsync().ConfigureAwait(false);
        }
        finally
        {
            // a pass cut short (its count cancelled as the source stops) still waits for the batches it started: they give
            // back their slots as they end, which is not after the slots are gone, and what they throw is not left unobserved
            await Task.WhenAll(running).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // a batch made when a slot is free; false (its items let go) once the store has failed as a whole
        async Task<bool> StartAsync(List<(TreeNode Node, TaskCompletionSource<ExceptionDispatchInfo?> Done)> taken)
        {
            try
            {
                await slots.WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                Let(taken);
                throw;
            }

            if (Volatile.Read(ref stopped) is not null)
            {
                slots.Release();
                Let(taken);
                return false;
            }

            running.Add(Task.Run(async () =>
            {
                try
                {
                    var failure = await MakeAsync(taken, keepFiles: false).ConfigureAwait(false);
                    if (Systemic(failure))
                    {
                        Interlocked.CompareExchange(ref stopped, failure, null);
                        return;
                    }

                    lock (tried)
                    {
                        tried.UnionWith(taken.Select(t => t.Node.Item!.Id));
                    }

                    listed(taken.Count);
                }
                finally
                {
                    slots.Release();
                }
            }, CancellationToken.None));
            return true;
        }

        void Let(List<(TreeNode Node, TaskCompletionSource<ExceptionDispatchInfo?> Done)> taken)
        {
            foreach (var (node, done) in taken)
            {
                Release(node, done, null);
            }
        }

        async Task<ExceptionDispatchInfo?> EndedAsync()
        {
            await Task.WhenAll(running).ConfigureAwait(false);
            return Volatile.Read(ref stopped);
        }
    }

    // --- listing and stat -------------------------------------------------------------------------------------------------

    public override async Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (path.Length == 0)
        {
            return VfsEntry.Directory("");
        }

        if (_tree.Resolve(path) is not { } found)
        {
            return null;
        }

        var (node, rest) = found;
        switch (node)
        {
            case DirNode dir:
                return dir.Entry;
            case AddressBooksNode { Books: var books }:
                return books.HasLists ? await RunAsync(() => books.StatAsync(rest, ct)).ConfigureAwait(false) : null;
            case ContactNode contact:
                return rest.Count == 0 ? await RunAsync(() => CardEntryAsync(contact, ct)).ConfigureAwait(false) : null;
            case ItemNode item when rest.Count == 0:
                return item.Entry;
            case ItemNode item:
                {
                    // the names in an item's folder are known before it is opened: another (Explorer's desktop.ini) is not looked for
                    var inAttachments = rest[0].Equals(MailNames.AttachmentsFolder, StringComparison.OrdinalIgnoreCase);
                    if (rest.Count > 2 || rest.Count == 2 && !inAttachments || rest.Count == 1 && !inAttachments && !ItemFiles.NamesOf(item.Item).Contains(rest[0], StringComparer.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    var listing = await RunAsync(() => ListingAsync(item, ct)).ConfigureAwait(false);
                    return rest.Count switch
                    {
                        1 when listing.File(rest[0]) is { } file => listing.EntryOf(file),
                        1 when inAttachments && listing.HasAttachmentsFolder => listing.AttachmentsEntry,
                        2 when listing.Attachment(rest[1]) is { } attachment => listing.EntryOf(attachment),
                        _ => null,
                    };
                }

            default:
                return null;
        }
    }

    public override async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (_tree.Resolve(path) is not { } found)
        {
            throw VfsException.NotFound(path);
        }

        var (node, rest) = found;
        switch (node)
        {
            case DirNode dir:
                foreach (var child in dir.Children)
                {
                    if (child is AddressBooksNode { Books.HasLists: false })
                    {
                        continue;
                    }

                    yield return child is ContactNode contact ? await RunAsync(() => CardEntryAsync(contact, ct)).ConfigureAwait(false) : child.Entry;
                }

                break;
            case AddressBooksNode { Books: var books }:
                {
                    var entries = books.HasLists ? await RunAsync(() => books.ListAsync(rest, ct)).ConfigureAwait(false) : null;
                    if (entries is null)
                    {
                        throw books.HasLists && await RunAsync(() => books.StatAsync(rest, ct)).ConfigureAwait(false) is { IsDirectory: false } ? VfsException.NotADirectory(path) : VfsException.NotFound(path);
                    }

                    foreach (var entry in entries)
                    {
                        yield return entry;
                    }

                    break;
                }

            case ContactNode:
                throw VfsException.NotADirectory(path);
            case ItemNode item:
                {
                    var listing = await RunAsync(() => ListingAsync(item, ct)).ConfigureAwait(false);
                    if (rest.Count == 0)
                    {
                        foreach (var file in listing.Files)
                        {
                            yield return listing.EntryOf(file);
                        }

                        if (listing.HasAttachmentsFolder)
                        {
                            yield return listing.AttachmentsEntry;
                        }
                    }
                    else if (rest.Count == 1 && listing.HasAttachmentsFolder && rest[0].Equals(MailNames.AttachmentsFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var attachment in listing.Attachments)
                        {
                            yield return listing.EntryOf(attachment);
                        }
                    }
                    else if (rest.Count == 1 && listing.File(rest[0]) is not null || rest.Count == 2 && listing.Attachment(rest[1]) is not null)
                    {
                        throw VfsException.NotADirectory(path);
                    }
                    else
                    {
                        throw VfsException.NotFound(path);
                    }

                    break;
                }
        }
    }

    // --- open -------------------------------------------------------------------------------------------------------------

    public override async Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (mode != VfsOpenMode.Read)
        {
            RequireWrite("writing");
        }

        if (_tree.Resolve(path) is not { } found)
        {
            throw VfsException.NotFound(path);
        }

        var (node, rest) = found;
        switch (node)
        {
            case DirNode:
                throw VfsException.IsADirectory(path);
            case AddressBooksNode { Books: var books }:
                {
                    var vcard = books.HasLists ? await RunAsync(() => books.CardAsync(rest, ct)).ConfigureAwait(false) : null;
                    if (vcard is null)
                    {
                        throw books.HasLists && (rest.Count == 0 || rest.Count == 1 && books.Folder(rest[0]) is not null) ? VfsException.IsADirectory(path) : VfsException.NotFound(path);
                    }

                    return new MemoryFile(vcard, "an address book's entry as a vCard");
                }

            case ContactNode contact:
                if (rest.Count > 0)
                {
                    throw VfsException.NotFound(path);
                }

                var card = await RunAsync(() => FilesAsync(contact, ct)).ConfigureAwait(false);
                return new MemoryFile(card.Files[0].Bytes!, card.Files[0].What);
            case ItemNode item:
                {
                    if (rest.Count == 0 || rest.Count == 1 && rest[0].Equals(MailNames.AttachmentsFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        throw VfsException.IsADirectory(path);
                    }

                    var files = await RunAsync(() => FilesAsync(item, ct)).ConfigureAwait(false);
                    var file = rest.Count switch
                    {
                        1 => files.File(rest[0]),
                        2 when rest[0].Equals(MailNames.AttachmentsFolder, StringComparison.OrdinalIgnoreCase) => files.Attachment(rest[1]),
                        _ => null,
                    } ?? throw VfsException.NotFound(path);
                    return file.Bytes is { } bytes
                        ? new MemoryFile(bytes, file.What)
                        : new SeekableStreamFile(file.Length, openCt => RunAsync(() => file.Attachment!.OpenAsync(openCt)), file.What);
                }

            default:
                throw VfsException.NotFound(path);
        }
    }

    // --- items ------------------------------------------------------------------------------------------------------------

    /// <summary>An item's files as made a little while ago, from the item as its folder lists it now, with what they come to in memory.</summary>
    private sealed record KeptFiles(ItemFiles Files, string Stamp, DateTime At, long Bytes);

    /// <summary>
    /// An item's files: kept a while once made; on the first ask made with its neighbours' (<see cref="PrepareAsync"/>),
    /// or, when those could not give it, alone, for the store to say why.
    /// </summary>
    private async Task<ItemFiles> FilesAsync(TreeNode node, CancellationToken ct)
    {
        var item = node.Item!;
        if (Kept(item) is { } kept)
        {
            return kept;
        }

        await PrepareAsync(node, forFiles: true, ct).ConfigureAwait(false);
        return Kept(item) ?? await AloneAsync(node, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What an item's folder lists (a contact's card's length): the listing kept from when the item's files were last
    /// made, while its folder lists it as it was then; else made with its neighbours', or alone.
    /// </summary>
    private async Task<ItemListing> ListingAsync(TreeNode node, CancellationToken ct)
    {
        var item = node.Item!;
        if (_listings.For(item) is { } listing)
        {
            return listing;
        }

        // an S/MIME message's listing is not kept, but its files are, a while
        if (Kept(item) is { } kept)
        {
            return ItemListing.Of(kept);
        }

        await PrepareAsync(node, forFiles: false, ct).ConfigureAwait(false);
        return _listings.For(item) ?? ItemListing.Of(Kept(item) ?? await AloneAsync(node, ct).ConfigureAwait(false));
    }

    private async Task<VfsEntry> CardEntryAsync(ContactNode contact, CancellationToken ct)
    {
        var listing = await ListingAsync(contact, ct).ConfigureAwait(false);
        return VfsEntry.File(contact.Name, listing.Files[0].Length, listing.Modified, listing.Created);
    }

    /// <summary>
    /// Makes an item's files together with its neighbours' (spec 7.7): the item, then those after it in its folder (round
    /// to the first), at most <see cref="BatchItems"/> of them and <see cref="BatchBytes"/> by the sizes the folder lists,
    /// each without its files made (for a listing, without its listing either) and not being made. A mailbox opens them
    /// together, in one request and their attachments in another, so a walk through a month of messages asks once for
    /// every sixteen; a data file opens them in turn. Whoever asks for one of them meanwhile waits for that one, which is
    /// let go as soon as it is made. When the store fails them as a whole for a reason of its own (the network, the
    /// sign-in, a server too busy) they all fail with it; for another, or when it does not give one, that one is left to be
    /// opened alone, which says why: one bad item does not fail its neighbours.
    /// </summary>
    /// <param name="forFiles">Whether the item's files are wanted (a file opened), not only what its folder lists.</param>
    private async Task PrepareAsync(TreeNode node, bool forFiles, CancellationToken ct)
    {
        var item = node.Item!;
        while (true)
        {
            if (_pending.TryGetValue(item.Id, out var running))
            {
                Ahead(node, forFiles);
                Rethrow(await running.WaitAsync(ct).ConfigureAwait(false));
                return;
            }

            var done = new TaskCompletionSource<ExceptionDispatchInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(item.Id, done.Task))
            {
                continue;
            }

            // made by a batch that has just ended
            if (Ready(item, forFiles))
            {
                Release(node, done, null);
                return;
            }

            var batch = Neighbours(node, done, forFiles);
            _ = Task.Run(() => MakeAsync(batch));
            Ahead(node, forFiles);
            Rethrow(await done.Task.WaitAsync(ct).ConfigureAwait(false));
            return;
        }
    }

    /// <summary>Throws a batch's failure when it is the store's as a whole, which asking for each item alone would only meet again.</summary>
    private static void Rethrow(ExceptionDispatchInfo? failure)
    {
        if (Systemic(failure))
        {
            failure!.Throw();
        }
    }

    /// <summary>Whether a batch's failure is the store's as a whole (the network, the sign-in, a server too busy, the source stopping), not its items'.</summary>
    private static bool Systemic(ExceptionDispatchInfo? failure)
        => failure?.SourceException is HttpRequestException or SignInRequiredException or IOException or OperationCanceledException or EwsException { Transient: true } or EwsException { Code: "401" };

    /// <summary>
    /// Starts the batch after the one an item is in, from the first item after it in its folder neither made nor being
    /// made: a walk (FileLocator, a search) lists folders one at a time, so the next sixteen are on their way while it
    /// lists the ones it waited for. Nobody waits for it; an item of it asked for meanwhile waits for that one as for any.
    /// </summary>
    private void Ahead(TreeNode node, bool forFiles)
    {
        var siblings = node.Parent?.Children ?? [];
        var at = IndexIn(siblings, node);
        for (var step = 1; at >= 0 && step < siblings.Count; step++)
        {
            var next = siblings[(at + step) % siblings.Count];
            if (next.Item is not { } item || Ready(item, forFiles) || _pending.ContainsKey(item.Id))
            {
                continue;
            }

            var done = new TaskCompletionSource<ExceptionDispatchInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_pending.TryAdd(item.Id, done.Task))
            {
                var batch = Neighbours(next, done, forFiles);
                _ = Task.Run(() => MakeAsync(batch));
            }

            return;
        }
    }

    /// <summary>The item, then those after it in its folder (round to the first) still to be made and not being made, up to a batch, each taken (in <see cref="_pending"/>) with a waiter of its own.</summary>
    private List<(TreeNode Node, TaskCompletionSource<ExceptionDispatchInfo?> Done)> Neighbours(TreeNode node, TaskCompletionSource<ExceptionDispatchInfo?> done, bool forFiles)
    {
        var chosen = new List<(TreeNode, TaskCompletionSource<ExceptionDispatchInfo?>)> { (node, done) };
        var siblings = node.Parent?.Children ?? [];
        var at = IndexIn(siblings, node);
        var bytes = node.Item!.Size;
        for (var step = 1; at >= 0 && step < siblings.Count && chosen.Count < BatchItems; step++)
        {
            var next = siblings[(at + step) % siblings.Count];
            if (next.Item is not { } item || bytes + item.Size > BatchBytes || Ready(item, forFiles))
            {
                continue;
            }

            var waiter = new TaskCompletionSource<ExceptionDispatchInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_pending.TryAdd(item.Id, waiter.Task))
            {
                chosen.Add((next, waiter));
                bytes += item.Size;
            }
        }

        return chosen;
    }

    private static int IndexIn(IReadOnlyList<TreeNode> siblings, TreeNode node)
    {
        for (var i = 0; i < siblings.Count; i++)
        {
            if (ReferenceEquals(siblings[i], node))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Makes a batch's files, letting each one's waiters go as it is made; a failure of the whole is theirs, and returned.</summary>
    private async Task<ExceptionDispatchInfo?> MakeAsync(List<(TreeNode Node, TaskCompletionSource<ExceptionDispatchInfo?> Done)> batch, bool keepFiles = true)
    {
        var released = new bool[batch.Count];
        try
        {
            await MakeAsync([.. batch.Select(b => b.Node)], keepFiles, _stopping.Token, i =>
            {
                released[i] = true;
                Release(batch[i].Node, batch[i].Done, null);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var failure = ExceptionDispatchInfo.Capture(ex);
            for (var i = 0; i < batch.Count; i++)
            {
                if (!released[i])
                {
                    released[i] = true;
                    Release(batch[i].Node, batch[i].Done, failure);
                }
            }

            return failure;
        }
        finally
        {
            for (var i = 0; i < batch.Count; i++)
            {
                if (!released[i])
                {
                    Release(batch[i].Node, batch[i].Done, null);
                }
            }
        }

        return null;
    }

    /// <summary>Lets an item's waiters go, and the item be made by another batch.</summary>
    private void Release(TreeNode node, TaskCompletionSource<ExceptionDispatchInfo?> done, ExceptionDispatchInfo? failure)
    {
        _pending.TryRemove(new KeyValuePair<string, Task<ExceptionDispatchInfo?>>(node.Item!.Id, done.Task));
        done.TrySetResult(failure);
    }

    /// <summary>
    /// Opens the items (together where the store opens several at once, else in turn) and makes each one's files, keeping
    /// its listing (with <paramref name="keepFiles"/>, the files too, a while), and tells <paramref name="made"/> of each
    /// (by its place) as it is done with. One the store does not give, or whose files do not make, is left to be opened
    /// alone, which says why.
    /// </summary>
    private async Task MakeAsync(IReadOnlyList<TreeNode> batch, bool keepFiles, CancellationToken ct, Action<int>? made = null)
    {
        var items = batch.Select(n => n.Item!).ToList();
        var together = Store.OpensTogether ? await Store.OpenManyAsync([.. items.Select(i => i.Id)], ct).ConfigureAwait(false) : null;
        for (var i = 0; i < batch.Count; i++)
        {
            try
            {
                var item = together is null ? await Store.OpenAsync(items[i].Id, ct).ConfigureAwait(false) : together[i];
                if (item is not null)
                {
                    Made(items[i], await ItemFiles.BuildAsync(item, items[i], batch[i].Name, ct, _smimeKeys).ConfigureAwait(false), keepFiles);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogDebug("outlook: '{Name}' was not made with its neighbours ({Error}); it is opened alone when asked for", batch[i].Name, ex.Message);
            }
            finally
            {
                made?.Invoke(i);
            }
        }

        _ = KeepListingsAsync(now: false);
    }

    /// <summary>An item opened by itself and its files made and kept: what a batch did not give, so the store says why.</summary>
    private async Task<ItemFiles> AloneAsync(TreeNode node, CancellationToken ct)
    {
        var item = node.Item!;
        var opened = await Store.OpenAsync(item.Id, ct).ConfigureAwait(false) ?? throw VfsException.NotFound(node.Name);
        var files = await ItemFiles.BuildAsync(opened, item, node.Name, ct, _smimeKeys).ConfigureAwait(false);
        Made(item, files, keepFiles: true);
        return files;
    }

    /// <summary>An item's files just made: its listing kept, put right when the files came out otherwise; with <paramref name="keepFiles"/>, the files too, a while.</summary>
    private void Made(MailItemSummary item, ItemFiles files, bool keepFiles)
    {
        // an S/MIME message's for the session alone: what it shows depends on the certificates at hand
        Listed(item, ItemListing.Of(files), lasting: !SmimeReader.IsSmime(files.Item));

        if (keepFiles)
        {
            KeepFiles(item, files);
        }
    }

    /// <summary>Whether what is wanted of the item is at hand: its files, or (not <paramref name="forFiles"/>) its listing.</summary>
    private bool Ready(MailItemSummary item, bool forFiles) => Kept(item) is not null || !forFiles && _listings.For(item) is not null;

    /// <summary>The item's files made a little while ago from the item as its folder lists it now; null for none.</summary>
    private ItemFiles? Kept(MailItemSummary item)
        => _items.TryGetValue(item.Id, out var kept) && DateTime.UtcNow - kept.At < ItemsKeptFor && kept.Stamp == ItemListings.StampOf(item) ? kept.Files : null;

    /// <summary>Keeps an item's files a while: at most <see cref="ItemsKept"/> items and <see cref="ItemsKeptBytes"/> by their lengths, the oldest going first.</summary>
    private void KeepFiles(MailItemSummary item, ItemFiles files)
    {
        var now = DateTime.UtcNow;
        var bytes = files.Files.Sum(f => f.Length) + files.Attachments.Sum(f => f.Length);
        var kept = new KeptFiles(files, ItemListings.StampOf(item), now, bytes);
        while (true)
        {
            if (_items.TryGetValue(item.Id, out var old))
            {
                if (_items.TryUpdate(item.Id, kept, old))
                {
                    Interlocked.Add(ref _itemsBytes, bytes - old.Bytes);
                    break;
                }
            }
            else if (_items.TryAdd(item.Id, kept))
            {
                Interlocked.Add(ref _itemsBytes, bytes);
                break;
            }
        }

        if (_items.Count <= ItemsKept && Interlocked.Read(ref _itemsBytes) <= ItemsKeptBytes)
        {
            return;
        }

        lock (_trimGate)
        {
            foreach (var (id, old) in _items.OrderBy(e => e.Value.At).ToList())
            {
                if (now - old.At < ItemsKeptFor && _items.Count <= ItemsKept && Interlocked.Read(ref _itemsBytes) <= ItemsKeptBytes)
                {
                    break;
                }

                if (_items.TryRemove(new KeyValuePair<string, KeptFiles>(id, old)))
                {
                    Interlocked.Add(ref _itemsBytes, -old.Bytes);
                }
            }
        }
    }

    /// <summary>Forgets the items' files kept a while (a refresh: they are made again when next asked for).</summary>
    private void ForgetFiles()
    {
        foreach (var (id, old) in _items.ToList())
        {
            if (_items.TryRemove(new KeyValuePair<string, KeptFiles>(id, old)))
            {
                Interlocked.Add(ref _itemsBytes, -old.Bytes);
            }
        }
    }

    // --- health -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs a read of the store: trouble upstream becomes Unavailable and Degraded, something gone NotFound; a success clears
    /// Degraded. A data file's item that does not read is an I/O error of that item's alone: the rest of the file reads.
    /// </summary>
    private async Task<T> RunAsync<T>(Func<Task<T>> read)
    {
        try
        {
            var result = await read().ConfigureAwait(false);
            MarkHealthy();
            return result;
        }
        catch (PstException ex)
        {
            throw new VfsException(VfsError.IoError, $"the Outlook data file does not read here: {ex.Message}");
        }
        catch (EwsException ex) when (ex.IsNotFound)
        {
            throw VfsException.NotFound(ex.Message);
        }
        catch (EwsException ex)
        {
            MarkDegraded(ex.Message);
            throw VfsException.Unavailable(ex.Message);
        }
        catch (SignInRequiredException ex)
        {
            MarkDegraded(ex.Message);
            throw VfsException.Unavailable(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            MarkDegraded(ex.Message);
            throw VfsException.Unavailable($"the mailbox could not be reached: {ex.Message}");
        }
        catch (IOException ex)
        {
            MarkDegraded(ex.Message);
            throw VfsException.Unavailable($"the Outlook data file could not be read: {ex.Message}");
        }
    }

    private void MarkDegraded(string detail)
    {
        if (!_degraded && IsInitialised)
        {
            _degraded = true;
            Context.ReportHealth(ComponentHealth.Degraded, detail);
        }
    }

    private void MarkHealthy()
    {
        if (_degraded && IsInitialised)
        {
            _degraded = false;
            Context.ReportHealth(ComponentHealth.Ready);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _refreshing.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }

        // a listing of every item under way ends at the cancellation; what its last batches made is kept with the rest
        if (await _listAllGate.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
        {
            _listAllGate.Release();
        }

        if (IsInitialised)
        {
            await KeepListingsAsync(now: true, wait: true).ConfigureAwait(false);
        }

        _store?.Dispose();
        _directoryOwned?.Dispose();
        _stopping.Dispose();
        _refreshGate.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
