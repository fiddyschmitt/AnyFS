using System.Collections.Concurrent;
using AnyFS.Core.Status;
using AnyFS.Sdk;

namespace AnyFS.Core.Runtime;

/// <summary>
/// The live state of one configured source, shared by every collection that includes it: the running
/// <see cref="ISource"/> (null until initialised), its status, degraded notes, and the file count the
/// router has seen go by.
/// </summary>
public sealed class SourceHandle
{
    // The files the router has seen in each folder listed, by a hash of the folder's path, and their total kept as it
    // changes. The paths themselves were kept, and the total summed from them at every status read, which the window
    // makes every second for every source not yet counted: with a million folders seen, 179 MB held for as long as the
    // source ran, and 45 ms and 4 MB a read (the review of 21-Nov-2026, P10). Under its own lock.
    private readonly Dictionary<ulong, int> _seenPerDirectory = [];
    private long _filesSeen;
    private readonly ConcurrentDictionary<string, string> _notes = new(StringComparer.Ordinal);
    private ISource? _current;
    private ComponentStatus _status = ComponentStatus.DisabledStatus;

    public SourceHandle(Guid id, string name, string typeId)
    {
        Id = id;
        Name = name;
        TypeId = typeId;
    }

    public Guid Id { get; }

    public string Name { get; set; }

    public string TypeId { get; }

    /// <summary>
    /// The initialised source, or null while Starting/Failed/Disabled. Under the engine it is a
    /// <see cref="Operations.TrackedSource"/> around the plugin's own, so ask of it only what <see cref="ISource"/> offers.
    /// </summary>
    public ISource? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    public ComponentStatus Status
    {
        get => Volatile.Read(ref _status);
        set
        {
            Volatile.Write(ref _status, value);
            Changed?.Invoke(this);
        }
    }

    public event Action<SourceHandle>? Changed;

    public SourceCapabilities Capabilities => Current?.Capabilities ?? SourceCapabilities.ReadOnly;

    public bool IsUsable => Current is not null && Status.IsUsable;

    /// <summary>Distinct files the router has seen in listings (a count per listed directory, so it needs no file names).</summary>
    public long FilesSeen => Interlocked.Read(ref _filesSeen);

    /// <summary>Degraded notes from the router (shadowed entries, duplicate names), keyed so each is logged once.</summary>
    public IReadOnlyCollection<string> Notes => _notes.Values.ToList();

    /// <summary>The number shown in the lists: the larger of the source's own count and what went by, with whether it is exact.</summary>
    public (long Files, bool Complete) FileCount()
    {
        var source = Current;
        if (source is null)
        {
            return (0, false);
        }

        var known = source.Counters.FilesKnown;
        var complete = source.Counters.State == EnumerationState.Complete;
        return (complete ? known : Math.Max(known, FilesSeen), complete);
    }

    public void RecordListing(string directory, int fileCount)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var key = Key(directory);
        lock (_seenPerDirectory)
        {
            _seenPerDirectory.TryGetValue(key, out var was);
            _seenPerDirectory[key] = fileCount;
            Interlocked.Add(ref _filesSeen, fileCount - was);
        }
    }

    public void ForgetListings()
    {
        lock (_seenPerDirectory)
        {
            _seenPerDirectory.Clear();
            Interlocked.Exchange(ref _filesSeen, 0);
        }
    }

    /// <summary>
    /// A folder's path as 64 bits (FNV-1a over its characters): what two folders in many million million share, and all
    /// that costs is one of them left out of a number shown with a "+" while it is not yet exact.
    /// </summary>
    private static ulong Key(string directory)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in directory)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return hash;
    }

    /// <summary>Adds a note once; returns true the first time so the caller can log it.</summary>
    public bool AddNote(string key, string text)
    {
        if (!_notes.TryAdd(key, text))
        {
            return false;
        }

        Changed?.Invoke(this);
        return true;
    }

    public void ClearNotes(string keyPrefix)
    {
        foreach (var key in _notes.Keys.Where(k => k.StartsWith(keyPrefix, StringComparison.Ordinal)).ToList())
        {
            _notes.TryRemove(key, out _);
        }
    }

    /// <summary>The status to display: Degraded with the notes when the router has flagged something.</summary>
    public ComponentStatus EffectiveStatus
    {
        get
        {
            var status = Status;
            if (status.State == ComponentState.Ready && !_notes.IsEmpty)
            {
                return new ComponentStatus(ComponentState.Degraded, string.Join("; ", _notes.Values));
            }

            return status;
        }
    }
}
