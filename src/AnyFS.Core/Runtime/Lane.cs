namespace AnyFS.Core.Runtime;

/// <summary>
/// One thing at a time, in the order asked for: each piece of work starts when the one before it has ended, however it
/// ended. The engine has one for every source and every collection, for what may take long (a source stopped, a mount
/// started): two changes to one source keep their order, and a change to another waits for neither.
/// </summary>
internal sealed class Lane
{
    private readonly object _gate = new();
    private Task _last = Task.CompletedTask;

    /// <summary>Whether something asked for here has yet to end.</summary>
    public bool Busy => !Last.IsCompleted;

    /// <summary>What was asked for last: it has ended when everything asked for so far has.</summary>
    public Task Last
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    /// <summary>Asks for <paramref name="work"/> after what was asked for before it. The task is the work's own, with what it threw.</summary>
    public Task Then(Func<Task> work)
    {
        lock (_gate)
        {
            return _last = AfterAsync(_last, work);
        }
    }

    private static async Task AfterAsync(Task before, Func<Task> work)
    {
        // never on the thread that asked, which holds the engine's lock or is the window's: it comes back to it at once
        await before.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
        await work().ConfigureAwait(false);
    }
}
