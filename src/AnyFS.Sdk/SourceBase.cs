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
    private readonly object _healthGate = new();
    private readonly List<(string Name, string Detail)> _held = [];
    private ISourceContext? _context;

    // what a call found the upstream out of reach with (MarkDegraded), until a call reaches it again; whether there is one,
    // read without the lock by every call that succeeds
    private string? _unreachable;
    private int _isUnreachable;

    // the health last reported, so that nothing is said twice
    private (ComponentHealth Health, string? Detail) _said = (ComponentHealth.Ready, null);

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

    /// <summary>
    /// Reports the source <see cref="ComponentHealth.Degraded"/> with <paramref name="detail"/>, once, until
    /// <see cref="MarkHealthy"/>: for an upstream that cannot be reached, or a part of it that fails. Nothing before the
    /// source has started (a failure then is the start's to report).
    /// </summary>
    protected void MarkDegraded(string detail)
    {
        if (!IsInitialised)
        {
            return;
        }

        lock (_healthGate)
        {
            if (_unreachable is null)
            {
                _unreachable = detail;
                Volatile.Write(ref _isUnreachable, 1);
                Say();
            }
        }
    }

    /// <summary>
    /// The upstream was reached again after <see cref="MarkDegraded"/>: the source is <see cref="ComponentHealth.Ready"/>,
    /// or, while something is held against it (<see cref="HoldDegraded(string, string?)"/>), Degraded with that alone.
    /// Costs nothing while the source is not marked: it may be called for every answer.
    /// </summary>
    protected void MarkHealthy()
    {
        if (!IsInitialised || Volatile.Read(ref _isUnreachable) == 0)
        {
            return;
        }

        lock (_healthGate)
        {
            if (_unreachable is not null)
            {
                _unreachable = null;
                Volatile.Write(ref _isUnreachable, 0);
                Say();
            }
        }
    }

    /// <summary>
    /// Holds the source <see cref="ComponentHealth.Degraded"/> with <paramref name="detail"/> until the source itself says
    /// that is over (null): for what waits on a later try or on the person (a write kept here that the upstream has not
    /// taken), which a call that succeeds meanwhile does not end as it ends <see cref="MarkDegraded"/>'s. One thing held
    /// this way; a source with several says each by name (<see cref="HoldDegraded(string, string?)"/>).
    /// </summary>
    protected void HoldDegraded(string? detail) => HoldDegraded("", detail);

    /// <summary>
    /// Holds the source <see cref="ComponentHealth.Degraded"/> with <paramref name="detail"/> for as long as what
    /// <paramref name="name"/> names is so, and lets it go with null. Several things may be held at once (writes that
    /// wait, sites that would not open, a list gone stale): each is said, in the order it began, after an upstream that
    /// cannot be reached; each lasts until it is itself over; and the source is Ready when nothing is held and the
    /// upstream is reached. There used to be one such thing at a time, the second taking the first's place, and one
    /// that ended while the upstream was out of reach stayed in what the tile said.
    /// </summary>
    protected void HoldDegraded(string name, string? detail)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsInitialised)
        {
            return;
        }

        lock (_healthGate)
        {
            var at = _held.FindIndex(h => h.Name == name);
            if (detail is null)
            {
                if (at < 0)
                {
                    return;
                }

                _held.RemoveAt(at);
            }
            else if (at < 0)
            {
                _held.Add((name, detail));
            }
            else if (_held[at].Detail == detail)
            {
                return;
            }
            else
            {
                _held[at] = (name, detail);
            }

            Say();
        }
    }

    /// <summary>Reports what the source's health comes to, where that is not what was last reported. Under the lock.</summary>
    private void Say()
    {
        var details = _unreachable is null ? _held.Select(h => h.Detail) : _held.Select(h => h.Detail).Prepend(_unreachable);
        var text = string.Join("; ", details);
        var now = text.Length == 0 ? (ComponentHealth.Ready, (string?)null) : (ComponentHealth.Degraded, text);
        if (now != _said)
        {
            _said = now;
            Context.ReportHealth(now.Item1, now.Item2);
        }
    }

    /// <summary>
    /// Whether <see cref="Guard{T}"/> marks the source by how the call it runs went. True unless the source is told of
    /// each request to its upstream as it goes, and marks itself by those: a call may succeed with nothing asked (a
    /// listing kept, a file held here), which says nothing of the upstream, and may hand back a file whose bytes are
    /// asked for only later, when a failure is no longer the call's.
    /// </summary>
    protected virtual bool HealthFollowsCalls => true;

    /// <summary>
    /// Runs a call to the upstream: the source is marked Degraded while the upstream cannot be reached
    /// (<see cref="VfsError.Unavailable"/>) and Ready again once a call succeeds. Any other failure is the call's own, and
    /// says nothing about the source's health.
    /// </summary>
    protected virtual async Task<T> Guard<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!HealthFollowsCalls)
        {
            return await work().ConfigureAwait(false);
        }

        try
        {
            var result = await work().ConfigureAwait(false);
            MarkHealthy();
            return result;
        }
        catch (VfsException ex) when (ex.Error == VfsError.Unavailable)
        {
            MarkDegraded(ex.Message);
            throw;
        }
    }
}
