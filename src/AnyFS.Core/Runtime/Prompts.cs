using System.Text;
using AnyFS.Sdk;

namespace AnyFS.Core.Runtime;

/// <summary>Where a source's <see cref="ISourceContext.PromptAsync"/> ends up: a console, a dialog, or nowhere.</summary>
public interface IPromptHandler
{
    Task<PromptResult> PromptAsync(string sourceName, PromptRequest request, CancellationToken ct);
}

/// <summary>Headless without a terminal: the source fails with "Needs input" and is retried only on demand (spec 6.4).</summary>
public sealed class NoPromptHandler : IPromptHandler
{
    public static readonly NoPromptHandler Instance = new();

    public Task<PromptResult> PromptAsync(string sourceName, PromptRequest request, CancellationToken ct)
        => throw new SourceFailedException($"Needs input: {request.Title}", permanent: true);
}

/// <summary>Prompts queue so two sources don't fight for the user: one at a time, in arrival order.</summary>
public sealed class SerialisingPromptHandler(IPromptHandler inner) : IPromptHandler
{
    private readonly SemaphoreSlim _one = new(1, 1);

    public int Waiting => Math.Max(0, _waiting);

    private int _waiting;

    public async Task<PromptResult> PromptAsync(string sourceName, PromptRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _waiting);
        try
        {
            await _one.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }

        try
        {
            return await inner.PromptAsync(sourceName, request, ct).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }
}

/// <summary><c>anyfs serve</c> with a terminal on stdin: prompts appear on the console.</summary>
public sealed class ConsolePromptHandler(TextReader? input = null, TextWriter? output = null) : IPromptHandler
{
    private readonly TextReader _in = input ?? Console.In;
    private readonly TextWriter _out = output ?? Console.Out;

    public Task<PromptResult> PromptAsync(string sourceName, PromptRequest request, CancellationToken ct)
    {
        _out.WriteLine();
        _out.WriteLine($"[{sourceName}] {request.Title}");
        if (!string.IsNullOrEmpty(request.Message))
        {
            _out.WriteLine(request.Message);
        }

        switch (request.Kind)
        {
            case PromptKind.YesNo:
                _out.Write("[y/n] ");
                var yn = ReadLine();
                return Task.FromResult(yn is null ? PromptResult.Cancelled : PromptResult.FromYesNo(yn.Trim().StartsWith('y')));

            case PromptKind.Choice:
                var choices = request.Choices ?? [];
                for (var i = 0; i < choices.Count; i++)
                {
                    _out.WriteLine($"  {i + 1}. {choices[i]}");
                }

                _out.Write($"[1-{choices.Count}] ");
                var pick = ReadLine();
                return Task.FromResult(int.TryParse(pick, out var n) && n >= 1 && n <= choices.Count ? PromptResult.FromChoice(n - 1) : PromptResult.Cancelled);

            case PromptKind.Password:
                _out.Write("> ");
                var secret = ReadMasked();
                return Task.FromResult(secret is null ? PromptResult.Cancelled : PromptResult.FromText(secret));

            default:
                _out.Write(string.IsNullOrEmpty(request.DefaultValue) ? "> " : $"[{request.DefaultValue}] > ");
                var text = ReadLine();
                if (text is null)
                {
                    return Task.FromResult(PromptResult.Cancelled);
                }

                return Task.FromResult(PromptResult.FromText(text.Length == 0 && request.DefaultValue is not null ? request.DefaultValue : text));
        }
    }

    private string? ReadLine() => _in.ReadLine();

    private string? ReadMasked()
    {
        if (!ReferenceEquals(_in, Console.In) || Console.IsInputRedirected)
        {
            return ReadLine();
        }

        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                _out.WriteLine();
                return sb.ToString();
            }

            if (key.Key == ConsoleKey.Escape)
            {
                _out.WriteLine();
                return null;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
            }
        }
    }
}
