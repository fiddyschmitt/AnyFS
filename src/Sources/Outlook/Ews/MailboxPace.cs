using System.Collections.Concurrent;

namespace AnyFS.Sources.Outlook.Ews;

/// <summary>
/// The pace a mailbox's requests go at (spec 7.7): Exchange's limits are per app per mailbox, a handful at a time and a
/// budget per ten minutes, so a few requests go at once and a hold the server asks for (429, 503, Retry-After) is kept
/// by everyone asking about that mailbox.
/// </summary>
internal sealed class MailboxPace
{
    private static readonly ConcurrentDictionary<string, MailboxPace> Paces = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _concurrent;
    private readonly object _gate = new();
    private DateTime _holdUntil = DateTime.MinValue;

    private MailboxPace(int concurrent)
    {
        _concurrent = new SemaphoreSlim(concurrent, concurrent);
    }

    /// <summary>The pace for a mailbox (its address), shared by all that ask for it.</summary>
    public static MailboxPace For(string mailbox) => Paces.GetOrAdd(mailbox, _ => new MailboxPace(4));

    /// <summary>Waits for a request's turn: past any hold, then for one of the slots.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                wait = _holdUntil - DateTime.UtcNow;
            }

            if (wait <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(wait, ct).ConfigureAwait(false);
        }

        await _concurrent.WaitAsync(ct).ConfigureAwait(false);
        return new Slot(_concurrent);
    }

    /// <summary>Holds every request for a while: what the server asked for, or a few seconds.</summary>
    public void Hold(TimeSpan? retryAfter)
    {
        var until = DateTime.UtcNow + (retryAfter is { } r && r > TimeSpan.Zero && r < TimeSpan.FromMinutes(10) ? r : TimeSpan.FromSeconds(5));
        lock (_gate)
        {
            if (until > _holdUntil)
            {
                _holdUntil = until;
            }
        }
    }

    private sealed class Slot(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
