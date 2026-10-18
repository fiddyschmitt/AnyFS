using AnyFS.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace AnyFS.Sources.SharePoint;

/// <summary>
/// A file open for writing on a source whose upstream takes whole uploads only: the bytes go to a local file
/// in the source's cache (pre-filled with the current content when the open mode keeps it), and the upload
/// happens when the last handle closes. Reads and writes are positional, as the SDK asks. Closing cannot
/// report an error to the client, so an upload that fails is logged and reported through the source's health.
/// </summary>
internal sealed class PendingUploadFile : IVfsFile
{
    private readonly string _path;
    private readonly string _tempPath;
    private readonly SafeFileHandle _handle;
    private readonly Func<string, long, CancellationToken, Task> _upload;
    private readonly Action<string, Exception> _onUploadFailed;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private long _length;
    private bool _dirty;
    private bool _disposed;
    private bool _discarded;
    private DateTimeOffset? _requestedCreated;
    private DateTimeOffset? _requestedModified;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="path">The source path, for messages.</param>
    /// <param name="tempPath">Where the bytes live until the upload; created or truncated unless <paramref name="keepExisting"/>.</param>
    /// <param name="keepExisting">The temp file already holds the current content (downloaded by the caller) and must be kept.</param>
    /// <param name="mustUpload">Upload even if nothing is written (a newly created empty file still has to exist upstream).</param>
    /// <param name="upload">(tempPath, length, ct) → uploads the file.</param>
    public PendingUploadFile(string path, string tempPath, bool keepExisting, bool mustUpload, Func<string, long, CancellationToken, Task> upload, Action<string, Exception> onUploadFailed, ILogger logger)
    {
        _path = path;
        _tempPath = tempPath;
        _upload = upload;
        _onUploadFailed = onUploadFailed;
        _logger = logger;
        _handle = File.OpenHandle(tempPath, keepExisting ? FileMode.Open : FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous);
        _length = keepExisting ? RandomAccess.GetLength(_handle) : 0;
        _dirty = mustUpload;
    }

    public long Length => Volatile.Read(ref _length);

    /// <summary>When the handle was opened: the created time the source reports while the file is pending.</summary>
    public DateTimeOffset Opened { get; } = DateTimeOffset.UtcNow;

    /// <summary>Run after the handle has closed and the upload (or its failure) is over: the source forgets the pending file.</summary>
    public Action? Closed { get; set; }

    /// <summary>True once the last handle has closed; the upload may still be in flight (<see cref="Completion"/>).</summary>
    public bool IsClosed => Volatile.Read(ref _disposed);

    /// <summary>
    /// Completes when the upload is over (or failed, or was discarded), after the source has forgotten the file:
    /// Windows posts a handle's close without waiting for it, so a rename, delete or re-open can arrive while
    /// the upload runs and must wait for it (18-Oct-2026: the E2E's rename right after a write on WinFsp).
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
        lock (_gate)
        {
            _requestedCreated = created ?? _requestedCreated;
            _requestedModified = modified ?? _requestedModified;
        }
    }

    /// <summary>The file was deleted while open: nothing is uploaded when the handle closes.</summary>
    public void Discard()
    {
        lock (_gate)
        {
            _discarded = true;
        }
    }

    /// <summary>
    /// A read-only view of the bytes written so far, for a second open of the file while it is pending (an
    /// attributes-only open, Explorer's preview, a reader): what SharePoint has is older or nothing. Disposing
    /// the view changes nothing here.
    /// </summary>
    public IVfsFile OpenReader() => new Reader(this);

    private sealed class Reader(PendingUploadFile owner) : IVfsFile
    {
        public long Length => owner.Length;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            if (owner._disposed)
            {
                throw VfsException.Unavailable($"'{owner._path}' was closed while being read; open it again");
            }

            return owner.ReadAsync(offset, buffer, ct);
        }

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct) => throw new VfsException(VfsError.AccessDenied, $"'{owner._path}' is open for writing elsewhere");

        public ValueTask SetLengthAsync(long length, CancellationToken ct) => throw new VfsException(VfsError.AccessDenied, $"'{owner._path}' is open for writing elsewhere");

        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0 || offset >= Length)
        {
            return ValueTask.FromResult(0);
        }

        return RandomAccess.ReadAsync(_handle, buffer, offset, ct);
    }

    public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await RandomAccess.WriteAsync(_handle, data, offset, ct).ConfigureAwait(false);
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            RandomAccess.SetLength(_handle, length);
            _length = length;
            _dirty = true;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken ct)
    {
        if (!_disposed)
        {
            RandomAccess.FlushToDisk(_handle);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the handle and, when anything changed, uploads; then removes the temp file.</summary>
    public async ValueTask DisposeAsync()
    {
        bool dirty;
        long length;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            dirty = _dirty && !_discarded;
            length = _length;
        }

        _handle.Dispose();
        try
        {
            if (dirty)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                await _upload(_tempPath, length, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is VfsException or IOException or OperationCanceledException or SignInRequiredException)
        {
            _logger.LogError(ex, "sharepoint: uploading '{Path}' ({Length} bytes) failed; the client's write is lost", _path, length);
            _onUploadFailed(_path, ex);
        }
        finally
        {
            try
            {
                File.Delete(_tempPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            Closed?.Invoke();
            _completion.TrySetResult();
        }
    }
}
