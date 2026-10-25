using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using AnyFS.Sdk;
using AnyFS.Sources.Outlook.Ews;
using AnyFS.Sources.Outlook.Mail;
using LibOutlook;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Outlook;

/// <summary>
/// An Outlook source (spec 7.7): a mailbox on Exchange Online through the web session, or a PST or OST file; either
/// way a mail store (<see cref="IMailStore"/>) whose folders and item names make the tree (<see cref="MailTree"/>),
/// kept in memory, and whose items are opened when their folder is, each item a folder with the item as Markdown and
/// .eml (or .ics, or a .vcf for a contact) and its attachments. Nothing of a message is written to disk. Every
/// <see cref="OutlookSettings.RefreshMinutes"/> the store is asked what changed (a file: when it changed on disk; a
/// mailbox: each folder's delta) and the tree swapped whole. Trouble upstream shows as Degraded until a read succeeds.
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
    private Task _refreshing = Task.CompletedTask;
    private (long Length, DateTime Written) _seen;
    private bool _degraded;

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
                    _store = await Task.Run(() => new PstMailStore(file, _settings.ShowHidden), ct).ConfigureAwait(false);
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

                    var (service, account, how) = await ConnectAsync(_settings, _standIn, Logger, interactive: true, message => Context.ReportProgress(message), ct).ConfigureAwait(false);
                    _store = new EwsMailStore(service, _settings.Kind == OutlookKind.Server ? $"{account} on {_settings.ServerUrl!.Host}" : account, _settings.ShowHidden, _settings.CalendarMonthsBack, _settings.CalendarMonthsAhead, Logger);
                    Logger.LogInformation("outlook: signed in as {Account} {How}", account, how);
                    break;
                }

            default:
                throw new SourceFailedException($"{file} is neither a mailbox's address, nor a server's, nor an Outlook data file (one ends in .pst or .ost).", permanent: true);
        }

        try
        {
            await _store.RefreshAsync(ct).ConfigureAwait(false);
            Swap(await MailTree.BuildAsync(_store, ct, (done, total, name) => Context.ReportProgress($"Listing {name} ({done} of {total} folders)", total == 0 ? null : (double)done / total)).ConfigureAwait(false));
        }
        catch (SignInRequiredException ex)
        {
            throw new SourceFailedException(ex.Message, permanent: ex.Permanent, ex);
        }
        catch (EwsException ex)
        {
            // a Windows sign-in the server refuses is the settings' fault; the rest may pass
            throw new SourceFailedException($"Listing the mailbox: {ex.Message}", permanent: ex.Code == "401" && _settings.Kind == OutlookKind.Server, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SourceFailedException($"Listing the mailbox: {ex.Message}", permanent: false, ex);
        }
        catch (PstException ex)
        {
            throw new SourceFailedException($"{Path.GetFileName(file)} could not be read as an Outlook data file: {ex.Message}", permanent: true, ex);
        }

        if (RefreshEvery > TimeSpan.Zero)
        {
            var stopping = _stopping.Token;
            _refreshing = Task.Run(() => RefreshLoopAsync(stopping), CancellationToken.None);
        }

        Logger.LogInformation("outlook: serving {Name}: {Folders} folders, {Items} items", _store.Name, _tree.FolderCount, _tree.ItemCount);
    }

    /// <summary>
    /// The EWS service for a mailbox, signed in: through the test's stand-in, or the web session's token read from
    /// the browser (a window when <paramref name="interactive"/> and the saved sign-in has lapsed). Also who the account
    /// is and how it signed in, for messages.
    /// </summary>
    internal static async Task<(IEwsService Service, string Account, string How)> ConnectAsync(OutlookSettings s, OutlookStandIn? standIn, ILogger logger, bool interactive, Action<string> progress, CancellationToken ct)
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
            Swap(await MailTree.BuildAsync(Store, ct, null).ConfigureAwait(false));
            _items.Clear();
            Logger.LogInformation("outlook: {Name} read again in {Seconds:F1} s: {Folders} folders, {Items} items", Store.Name, Stopwatch.GetElapsedTime(started).TotalSeconds, _tree.FolderCount, _tree.ItemCount);
            MarkHealthy();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RefreshEvery, ct).ConfigureAwait(false);
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
        Counters.Set(tree.FileCount, tree.FolderCount, EnumerationState.Complete);
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
                    yield return child is ContactNode contact ? await RunAsync(() => CardEntryAsync(contact, ct)).ConfigureAwait(false) : child.Entry;
                }

                break;
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
            return await ItemFiles.BuildAsync(opened, folderName, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>Runs a read of the store: trouble upstream becomes Unavailable and Degraded, something gone NotFound; a success clears Degraded.</summary>
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
            MarkDegraded(ex.Message);
            throw VfsException.Unavailable($"the Outlook data file could not be read: {ex.Message}");
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
