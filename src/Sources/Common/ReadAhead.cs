using System.Collections.Concurrent;

namespace AnyFS.Sources.Common;

/// <summary>
/// Whether a read of one thing (a page, an issue) should bring those beside it, and who waits for them. Those beside it are worth
/// reading when a client is going straight through them and asks for them next, and a waste, however many, when someone is
/// waiting for the one thing: a request of ten bodies takes ten times as long as one, on a server slow enough to notice. So a
/// listing goes by the listing before it in the same place, moments ago, and waits for them. An open, or a stat that has to list
/// a folder not kept yet, goes by the opens before it there, whoever is listing beside it (<see cref="ForOneThing"/>), and waits
/// for no others: where it is one of a run, a content search going through the files, which asks for the next as soon as it has
/// the last, those beside it come behind it, and the next waits for them if it is one of them. After a read of several fails (a
/// server too slow for so many at once), nothing is read together for a while.
/// </summary>
public sealed class ReadAhead(TimeProvider? time = null)
{
    // from one listing's read asked for to the next
    private static readonly TimeSpan ListedWithin = TimeSpan.FromSeconds(10);

    // from one open's answer, or its asking while it has none, to the next: a program's next comes at once, a person's does not
    private static readonly TimeSpan OpenedWithin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AloneFor = TimeSpan.FromMinutes(10);

    // the opens in a row that make a run: a run broken by a pause costs two more reads alone
    private const int OpensInARow = 3;

    // places remembered at most; past that they start again, which costs a read or two alone
    private const int PlacesKept = 2_000;

    // reads of several going at once that an open starts another behind: each holds a request slot, and whoever opens next is behind them
    private const int BehindAtOnce = 2;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly AsyncLocal<Opening?> _opening = new();
    private readonly ConcurrentDictionary<string, long> _listed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (long At, int Run)> _opened = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task>> _reading = new(StringComparer.OrdinalIgnoreCase);
    private long _aloneUntil;

    /// <summary>What this call goes on to read is for someone waiting for one thing, and when the result is disposed they have it.</summary>
    public IDisposable ForOneThing() => _opening.Value = new Opening(this);

    /// <summary>
    /// A read is about to be made in <paramref name="place"/> (where those beside it are: a page's parent, an issue's project):
    /// whether they should come with it.
    /// </summary>
    public bool Wanted(string place)
    {
        var now = _time.GetTimestamp();
        bool wanted;
        if (_opening.Value is { } opening)
        {
            opening.Place = place;
            var run = _opened.TryGetValue(place, out var last) && _time.GetElapsedTime(last.At, now) < OpenedWithin ? last.Run + 1 : 1;
            _opened[place] = (now, run);
            wanted = run >= OpensInARow;
            if (_opened.Count > PlacesKept)
            {
                _opened.Clear();
            }
        }
        else
        {
            wanted = _listed.TryGetValue(place, out var asked) && _time.GetElapsedTime(asked, now) < ListedWithin;
            _listed[place] = now;
            if (_listed.Count > PlacesKept)
            {
                _listed.Clear();
            }
        }

        return wanted && now >= Volatile.Read(ref _aloneUntil);
    }

    /// <summary>
    /// Reads those beside it, once however many ask for <paramref name="several"/> at once (<paramref name="read"/> keeps what it
    /// reads, and says when it has <see cref="Failed"/>). On return, what was asked for is kept if it was among them, or else is
    /// still to be read alone: a listing waits for them, an open only where they are already on their way.
    /// </summary>
    public async Task ReadAsync(string several, Func<Task> read, CancellationToken ct)
    {
        var onItsWay = _reading.ContainsKey(several);
        var forOneThing = _opening.Value is not null;
        if (forOneThing && !onItsWay && _reading.Count >= BehindAtOnce)
        {
            return;
        }

        var reading = _reading.GetOrAdd(several, s => new Lazy<Task>(() => RunAsync(s, read))).Value;
        if (!forOneThing || onItsWay)
        {
            await reading.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(string several, Func<Task> read)
    {
        await Task.Yield();
        try
        {
            await read().ConfigureAwait(false);
        }
        finally
        {
            _reading.TryRemove(several, out _);
        }
    }

    /// <summary>A read of several failed: the server takes too long over so many, or refuses one of them.</summary>
    public void Failed() => Volatile.Write(ref _aloneUntil, _time.GetTimestamp() + (long)(AloneFor.TotalSeconds * _time.TimestampFrequency));

    private sealed class Opening(ReadAhead owner) : IDisposable
    {
        public string? Place { get; set; }

        public void Dispose()
        {
            if (Place is { } place && owner._opened.TryGetValue(place, out var last))
            {
                owner._opened[place] = (owner._time.GetTimestamp(), last.Run);
            }
        }
    }
}
