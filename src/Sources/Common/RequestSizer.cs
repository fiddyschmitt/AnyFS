namespace AnyFS.Sources.Common;

/// <summary>
/// How many things to ask for in one request of a server whose pace is not known: more while requests answer in a few seconds, fewer
/// when they take long or fail, so a request of many whole things stays well inside the request timeout on a server that shows any
/// one of them in a few seconds, and a quick server is soon asked for as many as it allows.
/// </summary>
public sealed class RequestSizer(int smallest, int start, int largest)
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(30);

    private int _size = start;

    /// <summary>How many to ask for now.</summary>
    public int Size => Volatile.Read(ref _size);

    /// <summary>A request of <paramref name="asked"/> answered in <paramref name="time"/>.</summary>
    public void Took(TimeSpan time, int asked)
        => Volatile.Write(ref _size, time < Quick ? Math.Min(largest, Math.Max(asked, 1) * 2) : time > Slow ? Math.Max(smallest, asked / 2) : Math.Max(smallest, Math.Min(largest, asked)));

    /// <summary>A request of <paramref name="asked"/> that failed: the server may well be taking too long over so many.</summary>
    public void Failed(int asked) => Volatile.Write(ref _size, Math.Max(smallest, asked / 4));
}
