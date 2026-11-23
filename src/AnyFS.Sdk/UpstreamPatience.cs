namespace AnyFS.Sdk;

/// <summary>
/// How long the call under way waits on an upstream that sends it nothing: set by the host for a call a person's file
/// manager made (a stat, a listing, an open for reading), and null for a walk, a count and anything else a source does
/// by itself. The host keeps the limit by what the source reports of its requests
/// (<see cref="ISourceContext.UpstreamWatcher"/>): with requests out and nothing come back for this long, the call is
/// given up. A source reads it for two things the host cannot do for it.
/// <para>
/// A wait it makes before it sends anything because the upstream asked for it (a hold after a 429, a quota used up until
/// its window ends): one that would outlast the person's patience is refused at once, saying until when
/// (<see cref="ThrowIfOutlasted"/>), where a walk waits it out.
/// </para>
/// <para>
/// A fetch that callers share (a folder's listing, a request whose answer is kept): the host stops the call it gives
/// up, and the fetch that call was waiting on goes on, as it must for whoever else waits on it. But the next to ask
/// must not join it, or an upstream that answers again is not found until that one request's own time is up: where a
/// caller's wait ends by its token and <see cref="GivenUp"/> is true, the shared fetch gives up its place to a new one.
/// </para>
/// </summary>
public static class UpstreamPatience
{
    private static readonly AsyncLocal<Waiting?> OfThisCall = new();

    /// <summary>The limit for the call on this thread of work and all it starts; null where there is none.</summary>
    public static TimeSpan? Current
    {
        get => OfThisCall.Value?.Limit;
        set => OfThisCall.Value = value is { } limit ? new Waiting(limit) : null;
    }

    /// <summary>
    /// Whether the host has given the call under way up for an upstream that says nothing: true from just before the
    /// call's token is cancelled. False for a call cancelled for any other reason (its client went away), whose fetch
    /// is left for the next to join.
    /// </summary>
    public static bool GivenUp => OfThisCall.Value?.GivenUp == true;

    /// <summary>For a host: the call under way as it was put under its limit (<see cref="Put"/>); null where there is none.</summary>
    public static Waiting? Call => OfThisCall.Value;

    /// <summary>
    /// For a host: puts the call under way under a limit, for this thread of work and all it starts. A host may keep what
    /// more it knows of the call in a class of its own made from <see cref="Waiting"/>, and finds it again in <see cref="Call"/>.
    /// </summary>
    public static void Put(Waiting waiting) => OfThisCall.Value = waiting;

    /// <summary>
    /// Throws <see cref="VfsError.Unavailable"/> when a wait the upstream asked for is longer than the call under way
    /// waits for it; returns for a shorter one, and for any wait where there is no limit.
    /// </summary>
    /// <param name="wait">How long the upstream asked for no requests, from now.</param>
    /// <param name="who">Who asked, as the message's subject.</param>
    public static void ThrowIfOutlasted(TimeSpan wait, string who = "The server")
    {
        if (Current is { } patience && wait > patience)
        {
            throw VfsException.Unavailable($"{who} asked for no requests until {DateTime.Now + wait:HH:mm:ss}, which is longer than a person's request waits ({patience.TotalSeconds:N0} s); what is already listed still answers");
        }
    }

    /// <summary>One call under a limit, as the host that set it holds it.</summary>
    public class Waiting(TimeSpan limit)
    {
        private int _givenUp;

        public TimeSpan Limit { get; } = limit;

        public bool GivenUp => Volatile.Read(ref _givenUp) != 0;

        /// <summary>The host gives the call up: said before the call's token is cancelled, so that whatever the cancelling ends sees it.</summary>
        public void GiveUp() => Volatile.Write(ref _givenUp, 1);
    }
}
