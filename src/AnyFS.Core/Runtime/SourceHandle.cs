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
    private readonly ConcurrentDictionary<string, int> _seenPerDirectory = new(StringComparer.Ordinal);
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

    /// <summary>The initialised source, or null while Starting/Failed/Disabled.</summary>
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
    public long FilesSeen => _seenPerDirectory.Values.Sum(v => (long)v);

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

    public void RecordListing(string directory, int fileCount) => _seenPerDirectory[directory] = fileCount;

    public void ForgetListings() => _seenPerDirectory.Clear();

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
