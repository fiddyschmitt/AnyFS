using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AnyFS.Mounts.Tests;

/// <summary>A mount's log lines, kept for the test to look at and written to its output as they come.</summary>
internal sealed class RecordedLog : ILogger
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _lines = new();

    public (LogLevel Level, string Message)[] Lines => [.. _lines];

    /// <summary>The lines at <paramref name="level"/> that name <paramref name="text"/>.</summary>
    public string[] At(LogLevel level, string text) => [.. _lines.Where(l => l.Level == level && l.Message.Contains(text, StringComparison.Ordinal)).Select(l => l.Message)];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        _lines.Enqueue((logLevel, message));
        Console.WriteLine($"[server {logLevel}] {message}{(exception is null ? "" : " " + exception.Message)}");
    }
}
