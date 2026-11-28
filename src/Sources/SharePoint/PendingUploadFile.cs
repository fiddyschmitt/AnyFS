using System.Collections.Concurrent;
using System.Text.Json;
using AnyFS.Sdk;
using AnyFS.Sources.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace AnyFS.Sources.SharePoint;

/// <summary>How a file that was open for writing ended, for the source that kept track of it.</summary>
internal enum UploadEnd
{
    /// <summary>SharePoint has it.</summary>
    Landed,

    /// <summary>Nothing was written through it, so nothing went.</summary>
    Unchanged,

    /// <summary>The write was given up (<see cref="IVfsFile.AbandonAsync"/>): nothing went, and what was there before stays.</summary>
    GivenUp,

    /// <summary>The file was deleted while its bytes were here.</summary>
    Deleted,

    /// <summary>SharePoint refused it in a way no later try can mend (<see cref="PendingUploadFile.NeverLands"/>): nothing of it is kept.</summary>
    Refused,
}

/// <summary>
/// A file open for writing on a source whose upstream takes whole uploads only: the bytes go to a local file
/// in the source's cache (pre-filled with the current content when the open mode keeps it), and the upload
/// happens when the handle closes. Reads and writes are positional, as the SDK asks.
/// <para>
/// An upload that fails is not the end of the bytes (it was, with "the client's write is lost" in the log: review of
/// 21-Nov-2026). From the close on they are whole and are to land: a note beside the local file says where it goes, the
/// close's own try is told to who closed it (a mount that can says so to its client; Windows has told its own already),
/// and the file then waits here (<see cref="IsWaiting"/>), read from here meanwhile, for the source to try it again
/// (<see cref="TryAgainAsync"/>), in this run and, by its note, in the next (<see cref="Recover"/>). It ends when it has
/// landed, when the file is deleted, or when a later write of the same file takes its place.
/// </para>
/// <para>
/// That is for a failure that may pass (a lock, a throttle, a server that did not answer, a sign-in that lapsed). One
/// that waiting cannot mend (<see cref="NeverLands"/>: no permission to write there, the folder gone, a name SharePoint
/// refuses, a file there where a new one was to be made) is told to who closed the file and ends it: nothing is kept,
/// and nothing is tried again. Kept, such a file was tried every five minutes for good (the review's second pass,
/// 22-Nov-2026).
/// </para>
/// </summary>
internal sealed class PendingUploadFile : IVfsFile
{
    /// <summary>What a local file's note is called: the file's own name and this.</summary>
    public const string NoteSuffix = ".json";

    private const int NoteVersion = 1;

    // the local files this process has open for writing, whichever source opened them: a source that starts in
    // another's place leaves them alone (IsOpenHere)
    private static readonly ConcurrentDictionary<string, byte> OpenHere = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _key;
    private readonly Func<PendingUploadFile, CancellationToken, Task> _upload;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SafeFileHandle? _handle;    // the writer's, until it closes
    private SafeFileHandle? _reading;   // the readers', for as long as the bytes wait here after that
    private long _length;
    private bool _dirty;
    private bool _closed;
    private bool _waiting;
    private bool _over;
    private bool _landed;
    private bool _discarded;
    private int _tries;
    private string? _lastError;
    private DateTimeOffset _tried;
    private Task<Exception?>? _attempt;
    private CancellationTokenSource? _attemptStop;
    private DateTimeOffset? _requestedCreated;
    private DateTimeOffset? _requestedModified;

    /// <param name="path">The source path: where the upload goes.</param>
    /// <param name="tempPath">Where the bytes live until the upload; created or truncated unless <paramref name="keepExisting"/>.</param>
    /// <param name="keepExisting">The temp file already holds the current content (downloaded by the caller) and must be kept.</param>
    /// <param name="mustUpload">Upload even if nothing is written (a newly created empty file still has to exist upstream).</param>
    /// <param name="key">What the source serves (its site, account and library): the note carries it, and a source of another key leaves the file alone.</param>
    /// <param name="upload">Uploads this file's bytes (<see cref="TempPath"/>, <see cref="Length"/>) to <see cref="Path"/>.</param>
    public PendingUploadFile(string path, string tempPath, bool keepExisting, bool mustUpload, string key, Func<PendingUploadFile, CancellationToken, Task> upload, ILogger logger)
    {
        Path = path;
        TempPath = tempPath;
        _key = key;
        _upload = upload;
        _logger = logger;
        _handle = File.OpenHandle(tempPath, keepExisting ? FileMode.Open : FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous);
        _length = keepExisting ? RandomAccess.GetLength(_handle) : 0;
        _dirty = mustUpload;
        OpenHere[System.IO.Path.GetFullPath(tempPath)] = 0;
    }

    /// <summary>A file whose handle closed in an earlier run and whose upload did not land then.</summary>
    private PendingUploadFile(string path, string tempPath, long length, DateTimeOffset closed, DateTimeOffset? created, DateTimeOffset? modified, string key, Func<PendingUploadFile, CancellationToken, Task> upload, ILogger logger)
    {
        Path = path;
        TempPath = tempPath;
        _key = key;
        _upload = upload;
        _logger = logger;
        _length = length;
        _dirty = true;
        _closed = true;
        _waiting = true;
        _lastError = "it had not been uploaded when AnyFS last stopped";
        _requestedCreated = created;
        _requestedModified = modified;
        Opened = closed;
        _completion.TrySetResult();
    }

    /// <summary>
    /// Whether a local file is one a client still has open for writing in this process. A source that starts in the
    /// place of another (its settings edited, a Retry) finds that one's files in the folder, and a file still being
    /// written has no note yet: by this it is told from what a run that ended left half-written. Windows refuses to
    /// delete an open file and Linux and macOS do not, so nothing else told the two apart there.
    /// </summary>
    public static bool IsOpenHere(string tempPath) => OpenHere.ContainsKey(System.IO.Path.GetFullPath(tempPath));

    private void NoLongerOpen() => OpenHere.TryRemove(System.IO.Path.GetFullPath(TempPath), out _);

    /// <summary>The source path.</summary>
    public string Path { get; }

    /// <summary>The local file that holds the bytes.</summary>
    public string TempPath { get; }

    public long Length => Volatile.Read(ref _length);

    /// <summary>When the handle was opened: the created time the source reports while the file is pending.</summary>
    public DateTimeOffset Opened { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The file's end, told once: landed, nothing to upload, given up, or deleted. Not told of an upload that failed:
    /// the file goes on, waiting (<see cref="Waits"/>).
    /// </summary>
    public Action<PendingUploadFile, UploadEnd>? Ended { get; set; }

    /// <summary>An upload of it failed and it waits here for the next try: told with each failure.</summary>
    public Action<PendingUploadFile, Exception>? Waits { get; set; }

    /// <summary>
    /// An earlier write of the same file that waits to be uploaded and that this one was opened over. It is done with once
    /// this one has bytes of its own to land, or is deleted; given up or left unchanged, this one leaves it as it was, for
    /// the source to go on with.
    /// </summary>
    public PendingUploadFile? Supersedes { get; init; }

    /// <summary>There was no file of this name when it was opened: the name is here alone until the upload lands.</summary>
    public bool IsNew { get; init; }

    /// <summary>
    /// The file was opened as one that must be new (<see cref="VfsOpenMode.CreateNew"/>). That there was none went by the
    /// listing held here, which may be as old as the last walk: the upload asks SharePoint for a new file only, so a
    /// file made there since is not replaced.
    /// </summary>
    public bool OnlyIfNew { get; init; }

    /// <summary>
    /// Whether an upload's failure is one no later try can mend. Such a failure ends the file
    /// (<see cref="UploadEnd.Refused"/>) where any other leaves it waiting. The source's to say; none, and every failure waits.
    /// </summary>
    public Func<Exception, bool>? NeverLands { get; set; }

    /// <summary>True once the writer's handle has closed; the upload may still be in flight (<see cref="Completion"/>).</summary>
    public bool IsClosed => Volatile.Read(ref _closed);

    /// <summary>The handle has closed, the upload did not land, and the bytes wait here for another try.</summary>
    public bool IsWaiting
    {
        get
        {
            lock (_gate)
            {
                return _waiting && !_over && !_discarded;
            }
        }
    }

    /// <summary>What the last try said, for the person.</summary>
    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    /// <summary>How many uploads of it have failed in this run, and when the last one did.</summary>
    public (int Tries, DateTimeOffset At) Tried
    {
        get
        {
            lock (_gate)
            {
                return (_tries, _tried);
            }
        }
    }

    /// <summary>
    /// Completes when the close's own upload is over (landed, failed, or there was none), after the source has been told:
    /// Windows posts a handle's close without waiting for it, so a rename, delete or re-open can arrive while the upload
    /// runs and must wait for it (18-Oct-2026: the E2E's rename right after a write on WinFsp). A file that
    /// <see cref="IsWaiting"/> then has failed that upload.
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>Times a client set while the file was pending (<see cref="RequestTimes"/>); the source applies them after the upload.</summary>
    public (DateTimeOffset? Created, DateTimeOffset? Modified) RequestedTimes
    {
        get
        {
            lock (_gate)
            {
                return (_requestedCreated, _requestedModified);
            }
        }
    }

    public void RequestTimes(DateTimeOffset? created, DateTimeOffset? modified)
    {
        bool waiting;
        lock (_gate)
        {
            _requestedCreated = created ?? _requestedCreated;
            _requestedModified = modified ?? _requestedModified;
            waiting = _waiting && !_over;
        }

        if (waiting)
        {
            WriteNote();
        }
    }

    /// <summary>
    /// The file was deleted while its bytes were here: nothing of them is uploaded. One still open ends when its writer
    /// closes it; one that waits ends now, a try under way stopped and waited for first, so that the caller's own delete
    /// in SharePoint comes after anything the try did there.
    /// </summary>
    public async Task DiscardAsync()
    {
        Task? attempt;
        bool closed;
        lock (_gate)
        {
            _discarded = true;
            _attemptStop?.Cancel();
            attempt = _attempt;
            closed = _closed;
        }

        Supersedes?.Drop();
        if (!closed)
        {
            return;
        }

        if (attempt is not null)
        {
            await attempt.ConfigureAwait(false);
        }

        End(UploadEnd.Deleted);
    }

    /// <summary>Done with, without a word to the source: a later write of the same file has taken this one's place.</summary>
    private void Drop()
    {
        lock (_gate)
        {
            _discarded = true;
            _attemptStop?.Cancel();
        }

        End(UploadEnd.Deleted, tell: false);
    }

    /// <summary>
    /// A read-only view of the bytes here, for another open of the file while SharePoint has not got them (an
    /// attributes-only open, Explorer's preview, a reader): what SharePoint has is older or nothing. Disposing the view
    /// changes nothing here.
    /// </summary>
    public IVfsFile OpenReader() => new Reader(this);

    private sealed class Reader(PendingUploadFile owner) : IVfsFile
    {
        public long Length => owner.Length;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct) => owner.ReadHereAsync(offset, buffer, ct);

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct) => throw new VfsException(VfsError.AccessDenied, $"'{owner.Path}' is open for writing elsewhere");

        public ValueTask SetLengthAsync(long length, CancellationToken ct) => throw new VfsException(VfsError.AccessDenied, $"'{owner.Path}' is open for writing elsewhere");

        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A reader's read: through the writer's handle while it is open, through one of the readers' own once it has closed.</summary>
    private async ValueTask<int> ReadHereAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        SafeFileHandle handle;
        lock (_gate)
        {
            if (_over)
            {
                throw VfsException.Unavailable($"'{Path}' was closed while being read; open it again");
            }

            handle = _handle ?? (_reading ??= File.OpenHandle(TempPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.Asynchronous));
        }

        if (buffer.Length == 0 || offset >= Length)
        {
            return 0;
        }

        try
        {
            return await RandomAccess.ReadAsync(handle, buffer, offset, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // the writer closed, or the file ended, in the middle of this read
            throw VfsException.Unavailable($"'{Path}' was closed while being read; open it again");
        }
    }

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var handle = WritersHandle();
        if (buffer.Length == 0 || offset >= Length)
        {
            return ValueTask.FromResult(0);
        }

        return RandomAccess.ReadAsync(handle, buffer, offset, ct);
    }

    public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        await RandomAccess.WriteAsync(WritersHandle(), data, offset, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _dirty = true;
            if (offset + data.Length > _length)
            {
                _length = offset + data.Length;
            }
        }
    }

    public ValueTask SetLengthAsync(long length, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var handle = WritersHandle();
        lock (_gate)
        {
            RandomAccess.SetLength(handle, length);
            _length = length;
            _dirty = true;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken ct)
    {
        SafeFileHandle? handle;
        lock (_gate)
        {
            handle = _handle;
        }

        if (handle is not null)
        {
            RandomAccess.FlushToDisk(handle);
        }

        return ValueTask.CompletedTask;
    }

    private SafeFileHandle WritersHandle()
    {
        lock (_gate)
        {
            return _handle ?? throw new ObjectDisposedException(nameof(PendingUploadFile), $"'{Path}' has been closed");
        }
    }

    /// <summary>
    /// Closes the handle without keeping what was written through it: nothing is uploaded, the local file goes, and
    /// what SharePoint had stays as it was. For a write that broke off part way (an upload through a mount whose body
    /// ended early, a file deleted as it closes).
    /// </summary>
    public ValueTask AbandonAsync()
    {
        SafeFileHandle? handle;
        bool discarded;
        lock (_gate)
        {
            if (_closed)
            {
                return ValueTask.CompletedTask;
            }

            _closed = true;
            handle = _handle;
            _handle = null;
            discarded = _discarded;
        }

        handle?.Dispose();
        NoLongerOpen();
        End(discarded ? UploadEnd.Deleted : UploadEnd.GivenUp);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Closes the handle and, when anything changed, uploads. An upload that fails is thrown to who closed the file, and
    /// the bytes wait here to be tried again.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        SafeFileHandle? handle;
        bool upload;
        bool discarded;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            handle = _handle;
            _handle = null;
            discarded = _discarded;
            upload = _dirty && !_discarded;
        }

        handle?.Dispose();
        if (!upload)
        {
            NoLongerOpen();
            End(discarded ? UploadEnd.Deleted : UploadEnd.Unchanged);
            return;
        }

        // from here these bytes are whole and are to land: an older write of the file that waited is done with, and the
        // note says where these go, for a run after this one should this one end first. Open here until the note is
        // there: a source starting in another's place meanwhile took the bytes for half a file, with no note, and removed them
        Supersedes?.Drop();
        WriteNote();
        NoLongerOpen();
        if (IsOver)
        {
            // deleted in this very moment: its end came before the note, which goes after it
            Remove(TempPath + NoteSuffix);
            return;
        }

        if (await AttemptAsync(CancellationToken.None).ConfigureAwait(false) is { } failure)
        {
            throw failure as VfsException ?? new VfsException(VfsError.Unavailable, $"'{Path}' could not be uploaded: {failure.Message}", failure);
        }
    }

    /// <summary>
    /// Another try at the upload of a file that waits: true when it landed. One try at a time; a failure leaves it waiting.
    /// <paramref name="stop"/> ends the try with the file as it was, for the next run.
    /// </summary>
    public async Task<bool> TryAgainAsync(CancellationToken stop)
    {
        lock (_gate)
        {
            if (!_waiting || _over || _discarded)
            {
                return false;
            }
        }

        return await AttemptAsync(stop).ConfigureAwait(false) is null && HasLanded;
    }

    /// <summary>SharePoint has it: an upload of it landed.</summary>
    public bool HasLanded
    {
        get
        {
            lock (_gate)
            {
                return _landed;
            }
        }
    }

    private bool IsOver
    {
        get
        {
            lock (_gate)
            {
                return _over;
            }
        }
    }

    private Task<Exception?> AttemptAsync(CancellationToken stop)
    {
        lock (_gate)
        {
            return _attempt ??= RunAsync(stop);
        }
    }

    private async Task<Exception?> RunAsync(CancellationToken stop)
    {
        await Task.Yield();   // the caller holds the gate until it has the task
        CancellationTokenSource stopping;
        lock (_gate)
        {
            stopping = _attemptStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
            if (_discarded)
            {
                stopping.Cancel();
            }
        }

        Exception? failure = null;
        try
        {
            await _upload(this, stopping.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is VfsException or IOException or OperationCanceledException or SignInRequiredException or HttpRequestException or ObjectDisposedException or UnauthorizedAccessException)
        {
            failure = ex;
        }

        // a try that was stopped says nothing of the next one; any other failure may be one that waiting cannot mend
        var refused = failure is not null and not OperationCanceledException && !stop.IsCancellationRequested && NeverLands?.Invoke(failure) == true;
        bool discarded;
        lock (_gate)
        {
            _attemptStop = null;
            _attempt = null;
            discarded = _discarded;
            if (failure is not null && !discarded && !refused)
            {
                _waiting = true;
                _tried = DateTimeOffset.UtcNow;
                if (!stop.IsCancellationRequested)
                {
                    _tries++;
                    _lastError = failure is OperationCanceledException ? "the upload was stopped" : failure.Message;
                }
            }
        }

        stopping.Dispose();
        if (discarded)
        {
            // deleted while the upload ran: whoever deleted it ends it, and deletes what the upload may have left
            _completion.TrySetResult();
            return null;
        }

        if (failure is null)
        {
            End(UploadEnd.Landed);
            return null;
        }

        if (refused)
        {
            _logger.LogWarning("sharepoint: '{Path}' ({Length:N0} bytes) cannot be uploaded: {Error}. No later try can go better, so it is not kept", Path, Length, failure.Message);
            End(UploadEnd.Refused);
            return failure;
        }

        if (!stop.IsCancellationRequested)
        {
            _logger.LogWarning("sharepoint: uploading '{Path}' ({Length:N0} bytes) failed: {Error}. It is kept here and tried again", Path, Length, failure.Message);
        }

        try
        {
            Waits?.Invoke(this, failure);
        }
        finally
        {
            _completion.TrySetResult();
        }

        return failure;
    }

    /// <summary>The end, once: the local file and its note go, and the source is told.</summary>
    private void End(UploadEnd how, bool tell = true)
    {
        SafeFileHandle? reading;
        lock (_gate)
        {
            if (_over)
            {
                return;
            }

            _over = true;
            _waiting = false;
            _landed = how == UploadEnd.Landed;
            reading = _reading;
            _reading = null;
        }

        reading?.Dispose();
        Remove(TempPath);
        Remove(TempPath + NoteSuffix);
        NoLongerOpen();
        try
        {
            if (tell)
            {
                Ended?.Invoke(this, how);
            }
        }
        finally
        {
            _completion.TrySetResult();
        }
    }

    private static void Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // --- the note ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes the note beside the local file: where the bytes go, how many there are, and the times asked for. Whole or
    /// not at all (written aside and moved into place). A note that cannot be written is said: the upload goes on, and
    /// only a run that ends before it lands would lose it.
    /// </summary>
    private void WriteNote()
    {
        var note = TempPath + NoteSuffix;
        var aside = note + ".tmp";
        try
        {
            var (created, modified) = RequestedTimes;
            using (var stream = new FileStream(aside, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                json.WriteNumber("version", NoteVersion);
                json.WriteString("key", _key);
                json.WriteString("path", Path);
                json.WriteNumber("length", Length);
                json.WriteString("closed", DateTimeOffset.UtcNow);
                if (created is { } c)
                {
                    json.WriteString("created", c);
                }

                if (modified is { } m)
                {
                    json.WriteString("modified", m);
                }

                json.WriteEndObject();
            }

            File.Move(aside, note, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("sharepoint: the note for the upload of '{Path}' could not be written ({Error}): should AnyFS stop before the upload lands, it is not tried again", Path, ex.Message);
        }
    }

    /// <summary>What <see cref="Recover"/> made of a local file and its note.</summary>
    internal enum Recovered
    {
        /// <summary>The file waits to be uploaded.</summary>
        Waiting,

        /// <summary>The note is another source's (another site, account or library than this one serves): left where it is.</summary>
        NotOurs,

        /// <summary>No note that can be read, or one that does not fit its file: not a whole write.</summary>
        Broken,
    }

    /// <summary>
    /// A local file an earlier run left with its note: a file closed then whose upload had not landed, back as one that
    /// waits. <paramref name="path"/> is the source path the note names, also when the note is not this source's.
    /// </summary>
    public static Recovered Recover(string tempPath, string key, Func<PendingUploadFile, CancellationToken, Task> upload, ILogger logger, out PendingUploadFile? file, out string? path)
    {
        file = null;
        path = null;
        try
        {
            // each value looked at for its kind first: one of another kind (a null for a number) threw past the catch below,
            // and so failed the source's start (the review of 21-Nov-2026)
            using var note = JsonDocument.Parse(File.ReadAllBytes(tempPath + NoteSuffix));
            var root = note.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.LongOrNull("version") != NoteVersion)
            {
                return Recovered.Broken;
            }

            path = root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var length = root.LongOrNull("length") ?? -1;
            if (string.IsNullOrEmpty(path) || length < 0 || new FileInfo(tempPath) is not { Exists: true } local || local.Length != length)
            {
                return Recovered.Broken;
            }

            if (!string.Equals(root.Str("key"), key, StringComparison.Ordinal))
            {
                return Recovered.NotOurs;
            }

            file = new PendingUploadFile(path, tempPath, length, Time(root, "closed") ?? local.LastWriteTimeUtc, Time(root, "created"), Time(root, "modified"), key, upload, logger);
            return Recovered.Waiting;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Recovered.Broken;
        }

        static DateTimeOffset? Time(JsonElement note, string name)
            => note.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var at) ? at : null;
    }
}
