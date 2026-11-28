using System.Collections.Concurrent;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using ReverseMarkdown;

namespace AnyFS.Sources.Common;

/// <summary>
/// HTML as Markdown, for every source that shows a server's rich text as a file (a page, a message, a comment): the
/// default flavour, tables, fenced code and nested lists the GitHub way, tags it does not know passed over. Four
/// sources each kept a converter of their own with the same settings, each behind a lock of its own (the review of
/// 21-Nov-2026, C7).
/// <para>
/// The converter goes down an element's children by calling itself, so how deep HTML may nest is how deep the thread's
/// stack is: 400 levels of <c>div</c> were converted and 800 ended the program, with a stack overflow that nothing can
/// catch. A long thread of mail nests so, each reply wrapping the one before. So everything is converted on one thread
/// with a stack of its own (<see cref="StackBytes"/>), one text at a time as the locks had it: 20,000 levels are
/// converted there. HTML that nests deeper still (<see cref="DeepestTaken"/>) is not given to the converter, and what
/// the converter throws on is not the caller's failure: both show as their text, with a line that says so.
/// </para>
/// </summary>
public static partial class HtmlMarkdown
{
    /// <summary>The stack the converting thread is given: address space, of which only what is gone down into is used.</summary>
    internal const int StackBytes = 256 * 1024 * 1024;

    /// <summary>How deep HTML may nest and still be converted; deeper, it is shown as its text.</summary>
    internal const int DeepestTaken = 20_000;

    /// <summary>The line that stands first where HTML is shown as its text and not as Markdown.</summary>
    internal const string Unconverted = "(This could not be made into Markdown; its text follows.)";

    private static readonly BlockingCollection<Work> Asked = [];
    private static readonly Lazy<Thread> Converting = new(Begin, LazyThreadSafetyMode.ExecutionAndPublication);

    [ThreadStatic]
    private static bool _converting;

    // the converting thread's alone
    private static Converter? _converter;

    /// <summary>
    /// <paramref name="html"/> as Markdown. Never throws for what the HTML is: where it nests too deep or the converter
    /// fails on it, its text comes back under <see cref="Unconverted"/>.
    /// </summary>
    public static string Convert(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return Deep(() => ConvertHere(html));
    }

    /// <summary>
    /// Runs <paramref name="make"/> on the converting thread and gives what it gives, or throws what it throws: for a
    /// source's own work around the conversion (parsing the HTML, rewriting its links), which goes down the same tree.
    /// One at a time; called on the converting thread already, it runs in place.
    /// </summary>
    public static T Deep<T>(Func<T> make)
    {
        ArgumentNullException.ThrowIfNull(make);
        if (_converting)
        {
            return make();
        }

        _ = Converting.Value;
        using var work = new Work(() => make());
        Asked.Add(work);
        return (T)work.Wait()!;
    }

    /// <summary>
    /// How deep <paramref name="html"/> nests at the most, by its tags as they stand: never less than it does. Tags that
    /// hold nothing (<c>br</c>, <c>img</c>) and those a sibling closes unasked (<c>p</c>, <c>li</c>, a table's rows and
    /// cells) are not counted, so a long text of unclosed paragraphs is not taken for a deep one.
    /// </summary>
    internal static int Nesting(string html)
    {
        var depth = 0;
        var most = 0;
        for (var at = html.IndexOf('<', StringComparison.Ordinal); at >= 0 && at + 1 < html.Length; at = html.IndexOf('<', at + 1))
        {
            var closing = html[at + 1] == '/';
            var start = closing ? at + 2 : at + 1;
            var end = start;
            while (end < html.Length && char.IsAsciiLetterOrDigit(html[end]))
            {
                end++;
            }

            if (end == start || Flat().IsMatch(html.AsSpan(start, end - start)))
            {
                continue;
            }

            if (closing)
            {
                depth = Math.Max(0, depth - 1);
            }
            else
            {
                most = Math.Max(most, ++depth);
            }
        }

        return most;
    }

    private static string ConvertHere(string html)
    {
        if (Nesting(html) > DeepestTaken)
        {
            return Text(html);
        }

        try
        {
            _converter ??= new Converter(new Config
            {
                Tags = { Unknown = Config.UnknownTagsOption.Bypass },
                Links = { SmartHref = true },
                Formatting = { RemoveComments = true, ListBulletChar = '-', OutputLineEnding = "\n" },
            });
            return _converter.Convert(html);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // a converter that failed part way may not be as it was
            _converter = null;
            return Text(html);
        }
    }

    /// <summary>What HTML says with its tags taken out, under the line that says it was not converted.</summary>
    private static string Text(string html)
    {
        var text = WebUtility.HtmlDecode(Tag().Replace(html, " "));
        return Unconverted + "\n\n" + Blank().Replace(text, "\n").Trim();
    }

    private static Thread Begin()
    {
        var thread = new Thread(() =>
        {
            _converting = true;
            foreach (var work in Asked.GetConsumingEnumerable())
            {
                work.Run();
            }
        }, StackBytes)
        {
            IsBackground = true,
            Name = "AnyFS HTML to Markdown",
        };
        thread.Start();
        return thread;
    }

    /// <summary>One thing asked of the converting thread, and the place its asker waits at.</summary>
    private sealed class Work(Func<object?> make) : IDisposable
    {
        private readonly ManualResetEventSlim _done = new(false);
        private object? _made;
        private ExceptionDispatchInfo? _thrown;

        public void Run()
        {
            try
            {
                _made = make();
            }
            catch (Exception ex)
            {
                _thrown = ExceptionDispatchInfo.Capture(ex);
            }

            _done.Set();
        }

        public object? Wait()
        {
            _done.Wait();
            _thrown?.Throw();
            return _made;
        }

        public void Dispose() => _done.Dispose();
    }

    [GeneratedRegex(@"^(?:br|hr|img|input|meta|link|area|base|col|embed|source|track|wbr|p|li|tr|td|th|dt|dd|option)$", RegexOptions.IgnoreCase)]
    private static partial Regex Flat();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t]*(?:\r?\n[ \t]*)+")]
    private static partial Regex Blank();
}
