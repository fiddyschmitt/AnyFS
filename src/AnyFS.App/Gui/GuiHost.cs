using AnyFS.App.Cli;
using AnyFS.Core.Logging;
using AnyFS.Core.Plugins;
using AnyFS.Core.Runtime;
using AnyFS.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AnyFS.App.Gui;

/// <summary>What the command line passes to the window.</summary>
public sealed record GuiOptions(string? SettingsDir = null, string? LogLevel = null);

/// <summary>
/// The engine as the window runs it: logging to anyfs.log only, prompts as dialogs, the same
/// run loop as <c>anyfs serve</c> (so status.json is kept fresh and <c>anyfs status</c> sees the window).
/// </summary>
public sealed class GuiHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    private bool _shutDown;

    private GuiHost(SettingsPaths paths, ILoggerFactory loggerFactory, AnyFsEngine engine, GuiPromptHandler prompts)
    {
        Paths = paths;
        LoggerFactory = loggerFactory;
        Logger = loggerFactory.CreateLogger("AnyFS");
        Engine = engine;
        Prompts = prompts;
    }

    public SettingsPaths Paths { get; }

    public ILoggerFactory LoggerFactory { get; }

    public ILogger Logger { get; }

    public AnyFsEngine Engine { get; }

    public GuiPromptHandler Prompts { get; }

    public bool IsShutDown => _shutDown;

    /// <summary>Resolves paths, opens the log, loads plugins and settings. Nothing starts until <see cref="Start"/>.</summary>
    public static GuiHost Create(GuiOptions options, PluginRegistry? registry = null)
    {
        var paths = SettingsPaths.Resolve(options.SettingsDir);
        var level = LoggingSetup.ParseLevel(options.LogLevel ?? Commands.ReadConfiguredLevel(paths));
        var loggerFactory = LoggingSetup.Create(level, paths.LogFile, console: false);
        var logger = loggerFactory.CreateLogger("AnyFS");
        logger.LogInformation("AnyFS {Version} window starting; settings in {Dir} ({Mode}); log at {Log}", AnyFsEngine.Version, paths.Settings, paths.Portable ? "portable" : "per user", paths.LogFile);

        registry ??= BuiltIns.CreateRegistry();
        var loaded = registry.LoadDirectory(paths.Plugins, logger);
        if (loaded > 0)
        {
            logger.LogInformation("loaded {Count} plugin type(s) from {Dir}", loaded, paths.Plugins);
        }

        var prompts = new GuiPromptHandler();
        var engine = new AnyFsEngine(paths, registry, loggerFactory, prompts);
        engine.Load();
        return new GuiHost(paths, loggerFactory, engine, prompts);
    }

    /// <summary>Starts every enabled collection in the background; failures are logged, never thrown at the window.</summary>
    public void Start()
    {
        if (_run is not null)
        {
            return;
        }

        _run = Task.Run(async () =>
        {
            try
            {
                await Engine.RunAsync(null, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "the engine stopped unexpectedly");
            }
        });
    }

    /// <summary>Stops mounts and sources, flushes stats, closes the log. Safe to call twice.</summary>
    public async Task ShutdownAsync()
    {
        if (_shutDown)
        {
            return;
        }

        _shutDown = true;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_run is not null)
        {
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Logger.LogWarning("shutdown did not finish within 15 s; exiting anyway");
            }
        }
        else
        {
            await Engine.DisposeAsync().ConfigureAwait(false);
        }

        Logger.LogInformation("window closed");
        LoggerFactory.Dispose();
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);
}
