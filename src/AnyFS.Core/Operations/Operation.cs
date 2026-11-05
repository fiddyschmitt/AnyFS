using AnyFS.Sdk;

namespace AnyFS.Core.Operations;

/// <summary>One operation as it stood when the snapshot was taken: what the Operations tab's rows are made from.</summary>
/// <param name="Id">Ascending in the order the operations began.</param>
/// <param name="Source">The source's name when the operation began.</param>
/// <param name="Path">The path asked for, relative to the source's root ("" for the root, and for a kind without a path).</param>
/// <param name="Detail">What else the caller said or found: a rename's target, "not found" for a stat.</param>
/// <param name="Duration">So far, while it runs.</param>
/// <param name="Error">Why it failed, or why it was cancelled when that is known.</param>
/// <param name="Bytes">Read or written through the file handle so far.</param>
/// <param name="Items">Entries a listing has given so far, files a count has found so far.</param>
/// <param name="At">While a count runs, the folder it is up to ("" for the root; <see cref="SourceCounters.CurrentFolder"/>); null when it is in none, and for anything else.</param>
/// <param name="What">What a server was asked, in a word or two (GET, SFTP list); null for what is named by its kind alone.</param>
/// <param name="Answer">What the server answered, where it went well ("200 OK"); null for anything else.</param>
public sealed record OperationRecord(
    long Id,
    Guid SourceId,
    string Source,
    OperationKind Kind,
    string Path,
    string? Detail,
    DateTimeOffset Started,
    TimeSpan Duration,
    OperationOutcome Outcome,
    string? Error,
    long Bytes,
    long Items,
    string? At = null,
    string? What = null,
    string? Answer = null)
{
    /// <summary>The words the window's Operations tab uses for it.</summary>
    public string Name => What ?? Kind.Describe();

    public OperationAskedOf AskedOf => Kind.AskedOf();
}

/// <summary>
/// An operation in flight, handed out by <see cref="OperationTracker.Begin"/>. The caller adds what goes by
/// (<see cref="AddBytes"/>, <see cref="AddItems"/>) and ends it once with <see cref="Complete"/>, <see cref="Fail"/> or
/// <see cref="Cancel"/>; a second end is ignored. A failure inside an operation that goes on regardless (one read of an
/// open file) is <see cref="Note"/>d, and the operation ends Failed with it when it is completed.
/// </summary>
public sealed class Operation
{
    private readonly OperationTracker _tracker;
    private readonly object _gate = new();
    private long _bytes;
    private long _items;
    private int _outcome = (int)OperationOutcome.Running;
    private string? _error;
    private string? _detail;
    private string? _answer;
    private (OperationOutcome Outcome, string? Error)? _noted;
    private SourceCounters? _following;

    internal Operation(OperationTracker tracker, long id, Guid sourceId, string source, OperationKind kind, string path, string? detail, string? what, DateTimeOffset started, long startedAt)
    {
        _tracker = tracker;
        Id = id;
        SourceId = sourceId;
        Source = source;
        Kind = kind;
        Path = path;
        _detail = detail;
        What = what;
        Started = started;
        StartedAt = startedAt;
    }

    public long Id { get; }

    public Guid SourceId { get; }

    public string Source { get; }

    public OperationKind Kind { get; }

    public string Path { get; }

    /// <summary>What a server was asked, in a word or two (GET, SFTP list); null for what is named by its kind alone.</summary>
    public string? What { get; }

    public DateTimeOffset Started { get; }

    /// <summary>A <see cref="TimeProvider"/> timestamp.</summary>
    internal long StartedAt { get; }

    /// <summary>A <see cref="TimeProvider"/> timestamp, 0 while it runs.</summary>
    internal long FinishedAt { get; private set; }

    public string? Detail
    {
        get => Volatile.Read(ref _detail);
        set => Volatile.Write(ref _detail, value);
    }

    public OperationOutcome Outcome => (OperationOutcome)Volatile.Read(ref _outcome);

    public bool IsFinished => Outcome != OperationOutcome.Running;

    public string? Error => Volatile.Read(ref _error);

    /// <summary>What the server answered, where it went well ("200 OK"): the row's result in place of "OK".</summary>
    public string? Answer
    {
        get => Volatile.Read(ref _answer);
        set => Volatile.Write(ref _answer, value);
    }

    public long Bytes => Interlocked.Read(ref _bytes);

    public long Items => Volatile.Read(ref _following)?.FilesKnown ?? Interlocked.Read(ref _items);

    public void AddBytes(long count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _bytes, count);
        }
    }

    public void AddItems(long count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _items, count);
        }
    }

    /// <summary>
    /// For an operation whose progress is a source's counters (a count, which goes on inside the source, out of the tracker's
    /// sight): until it ends, <see cref="Items"/> is their running total and its record says which folder they are in, read each
    /// time it is looked at. Their total as it ends stays on it.
    /// </summary>
    public void Follow(SourceCounters counters) => Volatile.Write(ref _following, counters);

    /// <summary>The call returned: the operation succeeded, unless a failure was <see cref="Note"/>d meanwhile.</summary>
    public void Complete() => End(null, null);

    /// <summary>The call threw: Cancelled when <paramref name="ct"/> asked for that, Failed otherwise.</summary>
    public void Fail(Exception ex, CancellationToken ct = default)
    {
        var (outcome, error) = Classify(ex, ct);
        End(outcome, error);
    }

    /// <summary>The caller gave up on it.</summary>
    public void Cancel(string? why = null) => End(OperationOutcome.Cancelled, why);

    /// <summary>A failure that does not end the operation (a read of an open file): remembered for <see cref="Complete"/>; the first one counts.</summary>
    public void Note(Exception ex, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _noted ??= Classify(ex, ct);
        }
    }

    /// <summary>A refusal, said now with the request still to finish (a server's 404, its body to come): remembered for <see cref="Complete"/>, as with a failure <see cref="Note(Exception, CancellationToken)"/>d.</summary>
    public void Note(string refusal)
    {
        lock (_gate)
        {
            _noted ??= (OperationOutcome.Failed, refusal);
        }
    }

    internal OperationRecord Snapshot(TimeProvider time)
    {
        // before the outcome: a row read as running says where it was, and one that has ended says nowhere
        var at = Volatile.Read(ref _following)?.CurrentFolder;
        var outcome = Outcome;
        return new(Id, SourceId, Source, Kind, Path, Detail, Started, Duration(time), outcome, Error, Bytes, Items, outcome == OperationOutcome.Running ? at : null, What, Answer);
    }

    internal TimeSpan Duration(TimeProvider time)
        => IsFinished ? time.GetElapsedTime(StartedAt, FinishedAt) : time.GetElapsedTime(StartedAt);

    /// <summary>
    /// What an exception means for the outcome: a cancellation the caller asked for is Cancelled; one the caller did
    /// not ask for is a failure (an HTTP client's timeout is a cancellation inside, with the timeout as its cause).
    /// </summary>
    internal static (OperationOutcome Outcome, string? Error) Classify(Exception ex, CancellationToken ct) => ex switch
    {
        OperationCanceledException when ct.IsCancellationRequested => (OperationOutcome.Cancelled, null),
        OperationCanceledException { InnerException: TimeoutException } => (OperationOutcome.Failed, "timed out"),
        OperationCanceledException => (OperationOutcome.Failed, ex.Message),
        VfsException => (OperationOutcome.Failed, ex.Message),
        _ => (OperationOutcome.Failed, ex.GetBaseException().Message),
    };

    /// <param name="outcome">Null for "the call returned": what was noted, else Succeeded.</param>
    private void End(OperationOutcome? outcome, string? error)
    {
        lock (_gate)
        {
            if (_outcome != (int)OperationOutcome.Running)
            {
                return;
            }

            if (outcome is null)
            {
                (outcome, error) = _noted ?? (OperationOutcome.Succeeded, null);
            }

            _error = error;

            // what it had reached stays, whatever the counters go on to do
            if (Volatile.Read(ref _following) is { } following)
            {
                Interlocked.Exchange(ref _items, following.FilesKnown);
                Volatile.Write(ref _following, null);
            }

            FinishedAt = _tracker.Timestamp();
            Volatile.Write(ref _outcome, (int)outcome.Value);
        }

        _tracker.OnFinished(this);
    }
}
