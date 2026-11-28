using AnyFS.Sdk;

namespace AnyFS.Sources.Common;

/// <summary>
/// How far a count has got through the parts it goes by (a server's projects, repositories, plans or spaces): each said
/// once as the walk comes to it, or as someone opens it first, with how much of the list is behind it. Parts come to at
/// once are said one at a time, so that what is said keeps the order of the count: counted and said apart, a smaller
/// number could be said after a larger one. While the server has asked AnyFS to wait (<see cref="Waiting"/>), the count
/// says so too, with the server's reason, so a tile that stands still says why. Four sources each had this as a class of
/// their own, and two had none and said nothing of how far a count had got (the review of 21-Nov-2026, C3).
/// </summary>
/// <param name="total">How many parts there are; null where that is not known, and the count says how many so far.</param>
/// <param name="parts">What the parts are called, in the plural: "repositories".</param>
/// <param name="product">Who asks for a wait, as the count says it: "GitLab".</param>
public sealed class PartsCounted(ISourceContext context, long? total, string parts, string? product = null)
{
    private readonly HashSet<string> _reached = new(StringComparer.Ordinal);
    private string? _said;
    private double? _fraction;
    private DateTime _heldUntil;
    private string _why = "";
    private bool _over;

    /// <summary>The walk has come to the part with this key (its id, its path): said once, however often it is come to.</summary>
    public void Reached(string part)
    {
        lock (_reached)
        {
            if (_over || !_reached.Add(part))
            {
                return;
            }

            long count = _reached.Count;
            if (total is > 0 and var known)
            {
                // the one it has come to is not behind it yet: the bar is full only as the count ends
                _said = $"Counting files: {Math.Min(count, known):N0} of {known:N0} {parts}";
                _fraction = (double)Math.Min(count - 1, known - 1) / known;
            }
            else
            {
                _said = $"Counting files: {count:N0} {parts} so far";
                _fraction = null;
            }

            Say();
        }
    }

    /// <summary>The server asked AnyFS to wait: said with how far the count has got, until the wait is over, when the count is said plainly again.</summary>
    public void Waiting(TimeSpan wait, string why)
    {
        lock (_reached)
        {
            _heldUntil = DateTime.UtcNow + wait;
            _why = why;
            if (_said is not null && !_over)
            {
                Say();
            }
        }

        _ = Task.Delay(wait + TimeSpan.FromMilliseconds(50)).ContinueWith(_ =>
        {
            lock (_reached)
            {
                if (!_over && _said is not null && DateTime.UtcNow >= _heldUntil)
                {
                    Say();
                }
            }
        }, TaskScheduler.Default);
    }

    /// <summary>The count is over: nothing more is said of it.</summary>
    public void Over()
    {
        lock (_reached)
        {
            _over = true;
        }
    }

    private void Say()
    {
        var waiting = DateTime.UtcNow < _heldUntil ? $", waiting {Math.Ceiling((_heldUntil - DateTime.UtcNow).TotalSeconds):N0} s as {product ?? "the server"} asks ({_why})" : "";
        context.ReportProgress(_said + waiting, _fraction);
    }
}
