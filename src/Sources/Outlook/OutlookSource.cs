using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
/// item a folder with the item as Markdown and .eml (or .ics, or a .vcf for a contact) and its attachments. Nothing of a
/// message is written to disk. Every <see cref="OutlookSettings.RefreshMinutes"/> the store is asked what changed (a
/// file: when it changed on disk; a mailbox: each folder's delta) and the tree swapped whole. Trouble upstream shows as
/// Degraded until a read succeeds.
/// </summary>
public sealed class OutlookSource : SourceBase
{
    private static readonly TimeSpan ItemsKeptFor = TimeSpan.FromMinutes(3);
    private const int ItemsKept = 512;

    private readonly OutlookSettings _settings;
    private readonly OutlookStandIn? _standIn;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ConcurrentDictionary<string, (Lazy<Task<ItemFiles>> Files, DateTime At)> _items = new(StringComparer.Ordinal);
    private volatile MailTree _tree = MailTree.Empty;
    private IMailStore? _store;
    private AddressBooks? _books;
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
                    var (service, account, how) = await ConnectAsync(_settings, _standIn, Logger, interactive: true, message => Context.ReportProgress(message), hooks, ct).ConfigureAwait(false);
                    _store = new EwsMailStore(service, _settings.Kind == OutlookKind.Server ? $"{account} on {_settings.ServerUrl!.Host}" : account, _settings.ShowHidden, _settings.CalendarMonthsBack, _settings.CalendarMonthsAhead, Logger);
                    if (service is IAddressBookService directory)
                    {
                        // the organisation's address lists, where the service reaches them (the web session's People)
                        _books = new AddressBooks(directory, _store.Name, Path.Combine(Context.StateDirectory, AddressBookIndexFile.FileName), Logger, DirectoryColdPause, _stopping.Token);
                        _books.Updated += Count;
                    }

                    Logger.LogInformation("outlook: signed in as {Account} {How}", account, how);
                    break;
                }

            default:
                throw new SourceFailedException($"{file} is neither a mailbox's address, nor a server's, nor an Outlook data file (one ends in .pst or .ost).", permanent: true);
        }

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

            Swap(await MailTree.BuildAsync(_store, ct, (done, total, name) => Context.ReportProgress($"Listing {name} ({done} of {total} folders)", total == 0 ? null : (double)done / total), _books?.Node).ConfigureAwait(false));
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
    internal static async Task<(IEwsService Service, string Account, string How)> ConnectAsync(OutlookSettings s, OutlookStandIn? standIn, ILogger logger, bool interactive, Action<string> progress, SignInHooks? hooks, CancellationToken ct)
    {
        if (s.Kind == OutlookKind.Server)
        {
            // an Exchange server: SOAP with a Windows sign-in, the person's own mailbox there
            var url = s.EwsUrl!;
            var who = WindowsAccount.Name(s.IsWindowsCurrent, s.EffectiveSignIn == OutlookSignIn.Windows ? s.Username : null);
            var endpoint = standIn is null ? url : new Uri(standIn.Origin, url.PathAndQuery);
            var soap = new SoapEwsService(endpoint, WindowsAccount.Credentials(s.IsWindowsCurrent, s.Username, s.Password), s.TrustAnyCertificate, standIn?.Handler(), $"{url.Host}|{who}", logger);
            progress($"Signing in to {url.Host} as {who}");
            return (soap, standIn?.Account ?? who, $"with a Windows account ({url.Host})");
        }

        var address = s.Address ?? throw new SourceFailedException("The mailbox's address is missing.", permanent: true);
        if (s.IsAppSignIn)
        {
            return await ConnectGraphAsync(s, address, standIn, logger, progress, hooks, ct).ConfigureAwait(false);
        }

        if (standIn is not null)
        {
            var service = new OwaJsonService(standIn.Origin, address, standIn.Token, standIn.Handler(), logger);
            return (service, standIn.Account, "through the web session (a stand-in)");
        }

        var browser = OutlookPlugin.BrowserFor(s) ?? throw new SourceFailedException("The web session needs Microsoft Edge, Google Chrome or Chromium installed, or the path of one under Browser.", permanent: true);
        progress($"Finding {address}'s cloud");
        var origin = (MicrosoftCloud.FromName(s.Cloud) ?? await MicrosoftCloud.FindAsync(address, null, ct).ConfigureAwait(false)).OutlookOrigin;
        var session = new OwaTokenSession(origin, browser, OutlookPlugin.ProfileDir(address), logger);
        progress($"Reading the sign-in to {origin.Host} from {session.BrowserName}");
        var first = await session.TokenAsync(interactive, progress, ct).ConfigureAwait(false);
        var account = session.Account ?? address;
        if (!string.Equals(account, address, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("outlook: the browser profile for {Address} is signed in as {Account}", address, account);
        }

        var live = new OwaJsonService(origin, address, async (renew, token) =>
        {
            if (renew)
            {
                session.Invalidate();
            }

            // a renewal shows no window: the source turns the lapse into Degraded and waits for a Retry
            return await session.TokenAsync(interactive: false, _ => { }, token).ConfigureAwait(false);
        }, null, logger);
        _ = first;
        return (live, account, $"through the web session ({session.BrowserName}, {origin.Host})");
    }

    /// <summary>
    /// Exchange Online after an app sign-in (spec 7.7.1): the cloud from the settings or the realm lookup, AnyFS's app or
    /// the organisation's own signed in to there (silently from the saved sign-in when it can, else the browser or a
    /// device code), then Microsoft Graph at the mailbox's pace.
    /// </summary>
    private static async Task<(IEwsService Service, string Account, string How)> ConnectGraphAsync(OutlookSettings s, string address, OutlookStandIn? standIn, ILogger logger, Action<string> progress, SignInHooks? hooks, CancellationToken ct)
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
        return (new GraphEwsService(graph, address, s.ShowHidden, logger), account, cloud.IsGlobal ? "with an app sign-in (Microsoft Graph)" : $"with an app sign-in (Microsoft Graph, {cloud.Name})");
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

            Swap(await MailTree.BuildAsync(Store, ct, null, _books?.Node).ConfigureAwait(false));
            _items.Clear();
            Logger.LogInformation("outlook: {Name} read again in {Seconds:F1} s: {Folders:N0} folders, {Items:N0} items", Store.Name, Stopwatch.GetElapsedTime(started).TotalSeconds, _tree.FolderCount, _tree.ItemCount);
            await KeepListingAsync(ct).ConfigureAwait(false);
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
        Count();
    }

    /// <summary>The counts: the tree's, and the address books' entries read so far.</summary>
    private void Count()
    {
        var tree = _tree;
        Counters.Set(tree.FileCount + (_books?.FileCount ?? 0), tree.FolderCount + (_books?.FolderCount ?? 0), EnumerationState.Complete);
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
                    var files = await RunAsync(() => FilesAsync(item.Item, item.Name, ct)).ConfigureAwait(false);
                    return rest.Count switch
                    {
                        1 when files.File(rest[0]) is { } file => files.EntryOf(file),
                        1 when files.HasAttachmentsFolder && rest[0].Equals(MailNames.AttachmentsFolder, StringComparison.OrdinalIgnoreCase) => files.AttachmentsEntry,
                        2 when rest[0].Equals(MailNames.AttachmentsFolder, StringComparison.OrdinalIgnoreCase) && files.Attachment(rest[1]) is { } attachment => files.EntryOf(attachment),
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
                    var files = await RunAsync(() => FilesAsync(item.Item, item.Name, ct)).ConfigureAwait(false);
                    if (rest.Count == 0)
                    {
                        foreach (var file in files.Files)
                        {
                            yield return files.EntryOf(file);
                        }

                        if (files.HasAttachmentsFolder)
                        {
                            yield return files.AttachmentsEntry;
                        }
                    }
                    else if (rest.Count == 1 && files.HasAttachmentsFolder && rest[0].Equals(MailNames.AttachmentsFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var attachment in files.Attachments)
                        {
                            yield return files.EntryOf(attachment);
                        }
                    }
                    else if (rest.Count == 1 && files.File(rest[0]) is not null || rest.Count == 2 && files.Attachment(rest[1]) is not null)
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

                var card = await RunAsync(() => FilesAsync(contact.Item, contact.Name, ct)).ConfigureAwait(false);
                return new MemoryFile(card.Files[0].Bytes!, card.Files[0].What);
            case ItemNode item:
                {
                    if (rest.Count == 0 || rest.Count == 1 && rest[0].Equals(MailNames.AttachmentsFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        throw VfsException.IsADirectory(path);
                    }

                    var files = await RunAsync(() => FilesAsync(item.Item, item.Name, ct)).ConfigureAwait(false);
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

    /// <summary>An item's files, made on the first ask and kept a while; the store is asked for the item again after that.</summary>
    private Task<ItemFiles> FilesAsync(MailItemSummary item, string folderName, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_items.TryGetValue(item.Id, out var kept) && now - kept.At < ItemsKeptFor && !(kept.Files.IsValueCreated && kept.Files.Value.IsFaulted))
        {
            return kept.Files.Value;
        }

        if (_items.Count >= ItemsKept)
        {
            foreach (var old in _items.Where(e => now - e.Value.At >= ItemsKeptFor).Select(e => e.Key).ToList())
            {
                _items.TryRemove(old, out _);
            }

            if (_items.Count >= ItemsKept)
            {
                _items.Clear();
            }
        }

        var lazy = new Lazy<Task<ItemFiles>>(async () =>
        {
            var opened = await Store.OpenAsync(item.Id, CancellationToken.None).ConfigureAwait(false) ?? throw VfsException.NotFound(folderName);
            return await ItemFiles.BuildAsync(opened, item, folderName, CancellationToken.None, _smimeKeys).ConfigureAwait(false);
        });
        _items[item.Id] = (lazy, now);
        return lazy.Value;
    }

    private async Task<VfsEntry> CardEntryAsync(ContactNode contact, CancellationToken ct)
    {
        var files = await FilesAsync(contact.Item, contact.Name, ct).ConfigureAwait(false);
        return files.EntryOf(files.Files[0]);
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

        _store?.Dispose();
        _stopping.Dispose();
        _refreshGate.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
