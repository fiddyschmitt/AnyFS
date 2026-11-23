using System.Diagnostics.CodeAnalysis;

namespace AnyFS.Sdk;

/// <summary>
/// Remembers that a place a source needs, a server or a folder, could not be reached. While that is remembered a caller is
/// refused at once (<see cref="VfsError.Unavailable"/>) with what the place was found unreachable with, where it would have
/// waited out a connect's timeout, or the system's own on a share that has died, to learn the same again, one caller behind
/// another. One caller every <see cref="RetryEvery"/> is let through to try for real, the others still refused at once for
/// as long as its try takes, and a try that reaches the place forgets it. Nothing tries in the background: the caller let
/// through is the only probe.
/// <para>
/// A caller takes a turn with <see cref="Enter"/> (or <see cref="TryEnter"/>) and holds it while it tries. What a try
/// found is said with <see cref="Remember"/> or <see cref="Forget"/>, by that caller or by anything else that tried the
/// place; a turn given back with neither said (its caller gave up) is the next caller's at once.
/// </para>
/// </summary>
/// <param name="retryEvery">How long after the place was found unreachable a caller is let through to try it (<see cref="RetryEvery"/> by default).</param>
public sealed class ReachMemory(TimeSpan? retryEvery = null, TimeProvider? time = null)
{
    /// <summary>How long after a place was found unreachable a caller is let through to try it again.</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _every = retryEvery ?? RetryEvery;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();

    // what the place was found unreachable with and when, null while nothing is remembered; the caller let through to try
    // it, until it gives its turn back; and the turn of everyone's while nothing is remembered. All under the gate
    private string? _message;
    private long _at;
    private Turn? _trying;
    private Turn? _free;

    /// <summary>The caller's turn to try the place, as <see cref="TryEnter"/> gives it; a caller refused is thrown its refusal.</summary>
    public Turn Enter() => TryEnter(out var turn, out var refusal) ? turn : throw refusal;

    /// <summary>
    /// The caller's turn to try the place, to be disposed when its try is over however it went: any caller's while nothing
    /// is remembered, and while the place is remembered as unreachable one caller's every <see cref="RetryEvery"/>, no
    /// second while the first still tries. Every other caller is refused, with what the place was found unreachable with
    /// and how often it is tried.
    /// </summary>
    public bool TryEnter([NotNullWhen(true)] out Turn? turn, [NotNullWhen(false)] out VfsException? refusal)
    {
        lock (_gate)
        {
            if (_message is null)
            {
                turn = _free ??= new Turn(this, letThrough: false);
                refusal = null;
                return true;
            }

            if (_trying is null && _time.GetElapsedTime(_at) >= _every)
            {
                turn = _trying = new Turn(this, letThrough: true);
                refusal = null;
                return true;
            }

            turn = null;
            refusal = Refusal(_message);
            return false;
        }
    }

    /// <summary>The place was found unreachable: remembered from now, with what the callers refused are told.</summary>
    public void Remember(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            _message = message;
            _at = _time.GetTimestamp();
        }
    }

    /// <summary>The place was reached: nothing is remembered, and every caller is let through again.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _message = null;
            _trying = null;
        }
    }

    private VfsException Refusal(string message) => VfsException.Unavailable($"{message} (tried again every {_every.TotalSeconds:0.#} s)");

    /// <summary>A caller's turn to try the place (<see cref="Enter"/>).</summary>
    public sealed class Turn : IDisposable
    {
        private readonly ReachMemory _memory;

        // the one caller let through while the place was remembered as unreachable; not so, a turn taken while nothing was
        private readonly bool _letThrough;

        internal Turn(ReachMemory memory, bool letThrough)
        {
            _memory = memory;
            _letThrough = letThrough;
        }

        /// <summary>
        /// For a caller that had to wait before its try, as behind a lock that lets one connect at a time: refused where
        /// the place has been found unreachable since it took its turn, as those who come after it are, and not left to
        /// find out the same by itself. The one caller let through is never refused here: its try is the one awaited.
        /// </summary>
        public void ThrowIfFoundSince()
        {
            if (_letThrough)
            {
                return;
            }

            VfsException refusal;
            lock (_memory._gate)
            {
                if (_memory._message is null)
                {
                    return;
                }

                refusal = _memory.Refusal(_memory._message);
            }

            throw refusal;
        }

        /// <summary>Gives the turn back: where this was the one caller let through, another may be.</summary>
        public void Dispose()
        {
            if (!_letThrough)
            {
                return;
            }

            lock (_memory._gate)
            {
                if (ReferenceEquals(_memory._trying, this))
                {
                    _memory._trying = null;
                }
            }
        }
    }
}
