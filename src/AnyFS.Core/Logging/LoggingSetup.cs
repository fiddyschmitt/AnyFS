using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Formatters;

namespace AnyFS.Core.Logging;

/// <summary>anyfs.log (rolling, 10 MB x 5) and, headless, the console; ZLogger underneath (spec 10).</summary>
public static class LoggingSetup
{
    public const long RollingBytes = 10L * 1024 * 1024;

    public const int KeepFiles = 5;

    private static int _unhandledLogged;

    public static LogLevel ParseLevel(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "info" or "information" => LogLevel.Information,
        "warn" or "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "critical" or "fatal" => LogLevel.Critical,
        "none" or "off" => LogLevel.None,
        _ => LogLevel.Information,
    };

    public static bool IsValidLevel(string? text) => text?.Trim().ToLowerInvariant() is "trace" or "debug" or "info" or "information" or "warn" or "warning" or "error" or "critical" or "fatal" or "none" or "off";

    /// <summary>Creates the factory; dispose it at exit so the file writer flushes.</summary>
    public static ILoggerFactory Create(LogLevel minimum, string? logFile, bool console)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(minimum);
            // framework noise (Kestrel, DataProtection) only when it matters; our own categories start with "AnyFS"
            builder.AddFilter("Microsoft", LogLevel.Warning);
            builder.AddFilter("System", LogLevel.Warning);

            if (logFile is not null)
            {
                var stream = new RollingLogStream(logFile, RollingBytes, KeepFiles);
                builder.AddZLoggerStream(stream, options => options.UsePlainTextFormatter(ConfigureFormatter));
            }

            if (console)
            {
                builder.AddZLoggerConsole(options =>
                {
                    options.LogToStandardErrorThreshold = LogLevel.Error;
                    options.UsePlainTextFormatter(ConfigureFormatter);
                });
            }
        });
    }

    /// <summary>
    /// Puts in the log what would otherwise leave no trace: an exception nothing caught (the process ends after it, so
    /// the factory is disposed to flush the file first) and a failed background task nothing awaited (marked as seen,
    /// so it stays a log line). Once per process; the host calls it when it opens the log.
    /// </summary>
    public static void LogUnhandledExceptions(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (Interlocked.Exchange(ref _unhandledLogged, 1) != 0)
        {
            return;
        }

        var logger = factory.CreateLogger("AnyFS");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                logger.LogCritical(e.ExceptionObject as Exception, "unhandled exception; the process {Fate}", e.IsTerminating ? "ends" : "goes on");
                if (e.IsTerminating)
                {
                    factory.Dispose();
                }
            }
            catch (Exception)
            {
                // the log is gone already (the host is shutting down): nothing more to say it with
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                logger.LogWarning(e.Exception, "a background task failed and nothing awaited it");
            }
            catch (Exception)
            {
            }

            e.SetObserved();
        };
    }

    private static void ConfigureFormatter(PlainTextZLoggerFormatter formatter)
    {
        formatter.SetPrefixFormatter($"{0:yyyy-MM-dd HH:mm:ss.fff} {1,-5} {2} ", (in MessageTemplate template, in LogInfo info)
            => template.Format(info.Timestamp.Local, LevelText(info.LogLevel), info.Category.Name));
        formatter.SetExceptionFormatter((writer, ex) => Utf8StringInterpolation.Utf8String.Format(writer, $"\n{ex}"));
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => "?",
    };
}
