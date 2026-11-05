using System.Collections.Concurrent;

namespace AnyFS.Sdk.Testing;

/// <summary>
/// A <see cref="IUpstreamWatcher"/> that remembers what a source asked of whatever is behind it, as the host's Operations tab
/// would show it. <see cref="TestSourceContext"/> gives every source under test one, so what its requests go through in tests
/// is what they go through in the host.
/// </summary>
public sealed class RecordingUpstreamWatcher : IUpstreamWatcher
{
    private readonly ConcurrentQueue<Request> _requests = new();

    /// <summary>Everything asked so far, in the order it was asked.</summary>
    public IReadOnlyList<Request> Requests => [.. _requests];

    /// <summary>What has not ended: after a source is done with its work, nothing should be left here.</summary>
    public IReadOnlyList<Request> Running => _requests.Where(r => !r.IsDone).ToList();

    public IWatchedRequest Asked(string what, string target)
    {
        var request = new Request(what, target);
        _requests.Enqueue(request);
        return request;
    }

    /// <summary>One request, as far as it has got.</summary>
    public sealed class Request(string what, string target) : IWatchedRequest
    {
        private long _bytes;
        private long _entries;
        private int _done;

        public string What { get; } = what;

        public string Target { get; } = target;

        public long Bytes => Interlocked.Read(ref _bytes);

        public long Entries => Interlocked.Read(ref _entries);

        /// <summary>What the server answered, as it was said.</summary>
        public string? Answer { get; private set; }

        public bool Refused { get; private set; }

        public Exception? Error { get; private set; }

        public bool IsDone => Volatile.Read(ref _done) != 0;

        public void AddBytes(long count) => Interlocked.Add(ref _bytes, count);

        public void AddEntries(long count) => Interlocked.Add(ref _entries, count);

        public void Answered(string answer, bool refused)
        {
            Answer = answer;
            Refused = refused;
        }

        public void Failed(Exception error, CancellationToken ct) => Error ??= error;

        public void Done() => Volatile.Write(ref _done, 1);

        public override string ToString() => $"{What} {Target}";
    }
}
