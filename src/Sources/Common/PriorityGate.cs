namespace AnyFS.Sources.Common;

/// <summary>
/// So many requests at once, as a semaphore, but with two queues for a slot: a client's request (a listing or an open
/// someone is waiting on) goes before a walk's (<see cref="AnyFS.Sdk.Walks"/>), however many of the walk's are waiting, so a
/// search over a source whose walk fills the slots is answered as fast as the server answers, not after the walk's queue
/// (the user, 6-Nov-2026: "prioritised to what FLP is requesting"). Within a queue, oldest first.
/// </summary>
public sealed class PriorityGate(int slots)
{
    private readonly object _gate = new();
    private readonly Queue<Waiter> _urgent = new();
    private readonly Queue<Waiter> _others = new();
    private int _free = slots;

    /// <summary>How many may go at once.</summary>
    public int Slots { get; } = slots;

    /// <summary>Waits for a slot: an <paramref name="urgent"/> request takes the next one free ahead of those that are not.</summary>
    public Task WaitAsync(bool urgent, CancellationToken ct)
    {
        Waiter waiter;
        lock (_gate)
        {
            if (_free > 0 && _urgent.Count == 0 && (urgent || _others.Count == 0))
            {
                _free--;
                return Task.CompletedTask;
            }

            waiter = new Waiter();
            (urgent ? _urgent : _others).Enqueue(waiter);
        }

        if (ct.CanBeCanceled)
        {
            waiter.Cancelling = ct.Register(static state => ((Waiter)state!).Taken.TrySetCanceled(), waiter);
        }

        return waiter.Taken.Task;
    }

    /// <summary>Gives a slot back: to the oldest urgent waiter, else the oldest other.</summary>
    public void Release()
    {
        while (true)
        {
            Waiter? next;
            lock (_gate)
            {
                next = _urgent.Count > 0 ? _urgent.Dequeue() : _others.Count > 0 ? _others.Dequeue() : null;
                if (next is null)
                {
                    _free++;
                    return;
                }
            }

            next.Cancelling.Dispose();
            if (next.Taken.TrySetResult())
            {
                return;
            }

            // one that gave up meanwhile: the slot goes to the next
        }
    }

    private sealed class Waiter
    {
        public TaskCompletionSource Taken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Cancelling { get; set; }
    }
}
