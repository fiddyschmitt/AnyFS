using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using AnyFS.Sdk;
using AnyFS.Sources.Outlook.Mail;
using LibOutlook;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Outlook;

/// <summary>
/// An Outlook source (spec 7.7) over a PST or OST file: the mailbox's folders, each item a folder with the item as
/// Markdown and .eml (or .ics, or a .vcf for a contact) and its attachments. The tree of folders and item names is built
/// from the file's listing tables at the start and kept in memory; an item's files are made when its folder is first
/// opened and kept a few minutes. Nothing of a message is written to disk. A file another program writes to (Outlook's
/// OST) is read again every <see cref="OutlookSettings.RefreshMinutes"/> when it changed; a read that finds the file
/// mid-write shows the source as Degraded until one succeeds.
/// </summary>
public sealed class OutlookSource : SourceBase
{
    private static readonly TimeSpan ItemsKeptFor = TimeSpan.FromMinutes(3);
    private const int ItemsKept = 512;

    private readonly OutlookSettings _settings;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ConcurrentDictionary<string, (Lazy<ItemFiles> Files, DateTime At)> _items = new(StringComparer.Ordinal);
    private volatile MailTree _tree = MailTree.Empty;
    private PstMailStore? _store;
    private Task _refreshing = Task.CompletedTask;
    private (long Length, DateTime Written) _seen;
    private bool _degraded;

    public OutlookSource(OutlookSettings settings)
    {
        _settings = settings;
    }

    public override SourceCapabilities Capabilities => new(CanWrite: false, CaseInsensitive: true);

    /// <summary>The tree served now (for tests).</summary>
    internal MailTree Tree => _tree;

    private PstMailStore Store => _store ?? throw VfsException.Unavailable("the Outlook data file is not open");

    private TimeSpan RefreshEvery => TimeSpan.FromMinutes(Math.Max(0, _settings.RefreshMinutes));

    protected override async Task OnInitialiseAsync(CancellationToken ct)
    {
        var file = _settings.File ?? throw new SourceFailedException("Give the path of a .pst or .ost file.", permanent: true);
        if (_settings.LooksLikeAMailbox)
        {
            throw new SourceFailedException("Mailboxes on a server are not served yet: give the path of a .pst or .ost file.", permanent: true);
        }

        if (!_settings.IsDataFile)
        {
            throw new SourceFailedException($"{file} is not an Outlook data file: one ends in .pst or .ost.", permanent: true);
        }

        if (!File.Exists(file))
        {
            throw new SourceFailedException($"No file at {file}.", permanent: false);
        }

        Context.ReportProgress($"Reading {Path.GetFileName(file)}");
        try
        {
            await Task.Run(() =>
            {
                _store = new PstMailStore(file, _settings.ShowHidden);
                _seen = Stamp(file);
                Swap(MailTree.Build(_store));
            }, ct).ConfigureAwait(false);
        }
        catch (PstException ex)
        {
            throw new SourceFailedException($"{Path.GetFileName(file)} could not be read as an Outlook data file: {ex.Message}", permanent: true, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SourceFailedException($"{file} could not be opened: {ex.Message}", permanent: false, ex);
        }

        if (RefreshEvery > TimeSpan.Zero)
        {
            var stopping = _stopping.Token;
            _refreshing = Task.Run(() => RefreshLoopAsync(stopping), CancellationToken.None);
        }

        Logger.LogInformation("outlook: serving {Name} from {File} ({Format}): {Folders} folders, {Items} items", Store.Name, file, Store.Format, _tree.FolderCount, _tree.ItemCount);
    }

    // --- refresh ----------------------------------------------------------------------------------------------------------

    private static (long, DateTime) Stamp(string file)
    {
        var info = new FileInfo(file);
        return (info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>Reads the file again when it changed since the last look; the tree is swapped whole and open items are made afresh.</summary>
    internal async Task RefreshAsync(CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
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

            var started = Stopwatch.GetTimestamp();
            await Task.Run(() =>
            {
                Store.Refresh();
                Swap(MailTree.Build(Store));
            }, ct).ConfigureAwait(false);
            _seen = stamp;
            _items.Clear();
            Logger.LogInformation("outlook: {File} changed; read again in {Seconds:F1} s: {Folders} folders, {Items} items", Path.GetFileName(file), Stopwatch.GetElapsedTime(started).TotalSeconds, _tree.FolderCount, _tree.ItemCount);
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
                Logger.LogWarning("outlook: reading the file again failed; the last listing is still served: {Error}", ex.Message);
                MarkDegraded($"the file could not be read again: {ex.Message}");
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
                return rest.Count == 0 ? await Run(() => CardEntry(contact), ct).ConfigureAwait(false) : null;
            case ItemNode item when rest.Count == 0:
                return item.Entry;
            case ItemNode item:
                {
                    var files = await Run(() => Files(item), ct).ConfigureAwait(false);
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
                    yield return child is ContactNode contact ? await Run(() => CardEntry(contact), ct).ConfigureAwait(false) : child.Entry;
                }

                break;
            case ContactNode:
                throw VfsException.NotADirectory(path);
            case ItemNode item:
                {
                    var files = await Run(() => Files(item), ct).ConfigureAwait(false);
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

                var card = await Run(() => Files(contact.Item, contact.Name), ct).ConfigureAwait(false);
                return new MemoryFile(card.Files[0].Bytes!, card.Files[0].What);
            case ItemNode item:
                {
                    if (rest.Count == 0 || rest.Count == 1 && rest[0].Equals(MailNames.AttachmentsFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        throw VfsException.IsADirectory(path);
                    }

                    var files = await Run(() => Files(item), ct).ConfigureAwait(false);
                    var file = rest.Count switch
                    {
                        1 => files.File(rest[0]),
                        2 when rest[0].Equals(MailNames.AttachmentsFolder, StringComparison.OrdinalIgnoreCase) => files.Attachment(rest[1]),
                        _ => null,
                    } ?? throw VfsException.NotFound(path);
                    return file.Bytes is { } bytes
                        ? new MemoryFile(bytes, file.What)
                        : new SeekableStreamFile(file.Length, () => Run(() => file.Attachment!.Open(), path), file.What);
                }

            default:
                throw VfsException.NotFound(path);
        }
    }

    // --- items ------------------------------------------------------------------------------------------------------------

    private ItemFiles Files(ItemNode node) => Files(node.Item, node.Name);

    /// <summary>An item's files, made on the first ask and kept a while; the store is asked for the item again after that.</summary>
    private ItemFiles Files(MailItemSummary item, string folderName)
    {
        var now = DateTime.UtcNow;
        if (_items.TryGetValue(item.Id, out var kept) && now - kept.At < ItemsKeptFor)
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

        var lazy = new Lazy<ItemFiles>(() =>
        {
            var opened = Store.Open(item.Id) ?? throw VfsException.NotFound(folderName);
            return ItemFiles.Build(opened, folderName);
        });
        _items[item.Id] = (lazy, now);
        try
        {
            return lazy.Value;
        }
        catch
        {
            _items.TryRemove(item.Id, out _);
            throw;
        }
    }

    private VfsEntry CardEntry(ContactNode contact)
    {
        var files = Files(contact.Item, contact.Name);
        return files.EntryOf(files.Files[0]);
    }

    // --- health -----------------------------------------------------------------------------------------------------------

    /// <summary>Runs a read of the file: a file caught mid-write or gone becomes Unavailable and Degraded; a success clears Degraded.</summary>
    private async Task<T> Run<T>(Func<T> read, CancellationToken ct)
    {
        try
        {
            var result = await Task.Run(read, ct).ConfigureAwait(false);
            MarkHealthy();
            return result;
        }
        catch (PstException ex)
        {
            MarkDegraded(ex.Message);
            throw VfsException.Unavailable($"the Outlook data file could not be read: {ex.Message}");
        }
        catch (IOException ex)
        {
            MarkDegraded(ex.Message);
            throw VfsException.Unavailable($"the Outlook data file could not be read: {ex.Message}");
        }
    }

    private T Run<T>(Func<T> read, string path)
    {
        try
        {
            return read();
        }
        catch (PstException ex)
        {
            MarkDegraded(ex.Message);
            throw VfsException.Unavailable($"'{path}' could not be read from the Outlook data file: {ex.Message}");
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
