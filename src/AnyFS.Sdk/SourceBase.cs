using Microsoft.Extensions.Logging;

namespace AnyFS.Sdk;

/// <summary>
/// The boring parts of a source: holds the context, derives <see cref="StatAsync"/> from a listing of the
/// parent, and refuses writes unless <see cref="Capabilities"/> says otherwise. A minimal read-only
/// plugin overrides <see cref="ListAsync"/> and <see cref="OpenAsync"/> (and <see cref="OnInitialiseAsync"/>
/// if it has anything to set up).
/// </summary>
public abstract class SourceBase : ISource
{
    private ISourceContext? _context;

    public virtual SourceCapabilities Capabilities => SourceCapabilities.ReadOnly;

    public SourceCounters Counters { get; } = new();

    /// <summary>Available after <see cref="InitialiseAsync"/> has been called.</summary>
    protected ISourceContext Context => _context ?? throw new InvalidOperationException("the source has not been initialised");

    protected ILogger Logger => Context.Logger;

    protected bool IsInitialised => _context is not null;

    public Task InitialiseAsync(ISourceContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        return OnInitialiseAsync(ct);
    }

    protected virtual Task OnInitialiseAsync(CancellationToken ct) => Task.CompletedTask;

    public virtual async Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (path.Length == 0)
        {
            return VfsEntry.Directory("");
        }

        var (parent, name) = VfsPath.SplitLast(path);
        try
        {
            await foreach (var entry in ListAsync(parent, ct).ConfigureAwait(false))
            {
                if (NameEquals(entry.Name, name))
                {
                    return entry;
                }
            }
        }
        catch (VfsException ex) when (ex.Error is VfsError.NotFound or VfsError.NotADirectory)
        {
        }

        return null;
    }

    public abstract IAsyncEnumerable<VfsEntry> ListAsync(string path, CancellationToken ct);

    public abstract Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct);

    public virtual Task CreateDirectoryAsync(string path, CancellationToken ct) => throw VfsException.NotSupported("creating directories");

    public virtual Task DeleteAsync(string path, CancellationToken ct) => throw VfsException.NotSupported("deleting");

    public virtual Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct) => throw VfsException.NotSupported("renaming");

    public virtual Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct) => throw VfsException.NotSupported("setting times");

    public virtual Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct) => Task.FromResult<VfsSpace?>(null);

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>Compares names the way this source does (see <see cref="SourceCapabilities.CaseInsensitive"/>).</summary>
    protected bool NameEquals(string a, string b)
        => string.Equals(a, b, Capabilities.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Throws <see cref="VfsError.NotSupported"/> when the source can't write; call it first in write operations.</summary>
    protected void RequireWrite(string operation)
    {
        if (!Capabilities.CanWrite)
        {
            throw VfsException.NotSupported(operation);
        }
    }
}
