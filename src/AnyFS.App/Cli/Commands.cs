using System.CommandLine;
using AnyFS.Core.Logging;
using AnyFS.Core.Runtime;
using AnyFS.Core.Settings;
using AnyFS.Core.Status;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.App.Cli;

/// <summary>The headless side: <c>anyfs serve</c> and <c>anyfs status</c> (spec 12).</summary>
internal static class Commands
{
    public static RootCommand BuildRootCommand(out Option<string?> settingsDirOption, out Option<string?> logLevelOption)
    {
        var settingsDir = new Option<string?>("--settings-dir")
        {
            Description = "Use this settings folder (state, cache, plugins and the log become its siblings).",
            Recursive = true,
        };
        var logLevel = new Option<string?>("--log-level")
        {
            Description = "trace, debug, info, warn or error (default: app.json's logLevel, else info).",
            Recursive = true,
        };
        logLevel.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string?>();
            if (value is not null && !LoggingSetup.IsValidLevel(value))
            {
                result.AddError($"'{value}' is not a log level; use trace, debug, info, warn or error.");
            }
        });

        var root = new RootCommand("AnyFS presents anything as a file system. Without a command it opens the window.") { settingsDir, logLevel };

        var collectionOption = new Option<string[]>("--collection")
        {
            Description = "Run only this collection (repeat for several).",
            AllowMultipleArgumentsPerToken = false,
        };
        var serve = new Command("serve", "Run the enabled collections headless until Ctrl+C or SIGTERM.") { collectionOption };
        serve.SetAction((parseResult, ct) => ServeAsync(parseResult.GetValue(settingsDir), parseResult.GetValue(logLevel), parseResult.GetValue(collectionOption), ct));
        root.Subcommands.Add(serve);

        var status = new Command("status", "Print collections, sources, mounts, their states and access paths. Exit code 0 when everything is green, 1 when not, 2 when anyfs is not running.");
        status.SetAction(parseResult => Status(parseResult.GetValue(settingsDir)));
        root.Subcommands.Add(status);

        var dllArgument = new Argument<string>("dll") { Description = "The plugin DLL to check." };
        var check = new Command("check", "Check a plugin DLL before a user does: its AnyFS.Sdk version, the framework assemblies it uses against the set the trimmed host keeps, the dependencies beside it, and whether it loads and its settings round-trip. Exit code 0 when nothing failed.") { dllArgument };
        check.SetAction(parseResult => PluginCheck(parseResult.GetValue(dllArgument)!));
        var plugin = new Command("plugin", "Plugin tools.") { check };
        root.Subcommands.Add(plugin);

        settingsDirOption = settingsDir;
        logLevelOption = logLevel;
        return root;
    }

    public static async Task<int> ServeAsync(string? settingsDir, string? logLevel, string[]? collections, CancellationToken ct)
    {
        var paths = SettingsPaths.Resolve(settingsDir);
        var level = LoggingSetup.ParseLevel(logLevel ?? ReadConfiguredLevel(paths));
        using var loggerFactory = LoggingSetup.Create(level, paths.LogFile, console: true);
        var logger = loggerFactory.CreateLogger("AnyFS");
        logger.LogInformation("AnyFS {Version} starting; settings in {Dir} ({Mode}); log at {Log}", AnyFsEngine.Version, paths.Settings, paths.Portable ? "portable" : "per user", paths.LogFile);

        var registry = BuiltIns.CreateRegistry();
        var loaded = registry.LoadDirectory(paths.Plugins, logger);
        if (loaded > 0)
        {
            logger.LogInformation("loaded {Count} plugin type(s) from {Dir}", loaded, paths.Plugins);
        }

        IPromptHandler prompts = Console.IsInputRedirected ? NoPromptHandler.Instance : new ConsolePromptHandler();
        await using var engine = new AnyFsEngine(paths, registry, loggerFactory, prompts);
        engine.Load();
        try
        {
            logger.LogInformation("press Ctrl+C to stop");
            await engine.RunAsync(collections is { Length: > 0 } ? collections : null, ct);
        }
        catch (ArgumentException ex)
        {
            logger.LogError("{Error}", ex.Message);
            return 2;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }

        logger.LogInformation("stopped");
        return 0;
    }

    /// <summary><c>anyfs plugin check &lt;dll&gt;</c>: PASS/WARN/FAIL lines, exit 1 on any FAIL (spec 14.1).</summary>
    public static int PluginCheck(string dll)
    {
        var report = AnyFS.Core.Plugins.PluginChecker.Check(dll);
        foreach (var line in report.Lines)
        {
            Console.Out.WriteLine(line.ToString());
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine(report.Passed
            ? $"ok: {report.Lines.Count} check(s), {report.Warnings} warning(s). Drop the DLL (with any files beside it) into the plugins folder next to the settings folder."
            : $"{report.Failures} check(s) failed, {report.Warnings} warning(s).");
        return report.Passed ? 0 : 1;
    }

    public static int Status(string? settingsDir)
    {
        var paths = SettingsPaths.Resolve(settingsDir);
        var (snapshot, running) = StatusFileReader.Read(paths);
        var registry = BuiltIns.CreateRegistry();
        registry.LoadDirectory(paths.Plugins, NullLogger.Instance);
        var out_ = Console.Out;

        if (!running || snapshot is null)
        {
            var store = new SettingsStore(paths, NullLogger.Instance);
            store.Load();
            out_.WriteLine($"anyfs is not running. Settings: {paths.Settings}");
            out_.WriteLine();
            PrintPlugins(out_, registry.Plugins.Select(p => (p.TypeId, p.Kind, p.DisplayName, p.Origin, p.Error)));
            out_.WriteLine();
            out_.WriteLine("Configured collections:");
            foreach (var collection in store.Collections)
            {
                out_.WriteLine($"  {collection.Name}{(collection.Enabled ? "" : " (disabled)")}");
                foreach (var membership in collection.Sources)
                {
                    var source = store.FindSource(membership.Id);
                    out_.WriteLine($"    source  {source?.Name ?? membership.Id.ToString()}  [{source?.Type}]  at '{membership.Path}'{(membership.ReadOnly ? "  read-only" : "")}{(source is { Enabled: false } ? "  (disabled)" : "")}");
                }

                foreach (var mount in collection.Mounts)
                {
                    out_.WriteLine($"    mount   {mount.Name}  [{mount.Provider}]{(mount.ReadOnly ? "  read-only" : "")}{(mount.Enabled ? "" : "  (disabled)")}");
                }
            }

            return 2;
        }

        out_.WriteLine($"anyfs {snapshot.Version} running (pid {snapshot.Pid}, since {snapshot.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}). Settings: {snapshot.SettingsDirectory}");
        out_.WriteLine();
        PrintPlugins(out_, snapshot.Plugins.Select(p => (p.TypeId, p.Kind, p.DisplayName, p.Origin, p.Error)));
        out_.WriteLine();
        foreach (var collection in snapshot.Collections)
        {
            out_.WriteLine($"{Circle(collection.Colour)} {collection.Name}: {collection.Text}");
            foreach (var source in collection.Sources)
            {
                var count = source.Files > 0 || source.FilesComplete ? $"  {source.Files:N0}{(source.FilesComplete ? "" : "+")} files" : "";
                out_.WriteLine($"    {Circle(ComponentStatus.ColourOf(Enum.Parse<ComponentState>(source.State)).ToString())} source {source.Name} [{source.Type}] at '{source.Path}'{(source.ReadOnly ? " read-only" : "")}: {StateText(source.State, source.Text)}{count}");
            }

            foreach (var mount in collection.Mounts)
            {
                out_.WriteLine($"    {Circle(ComponentStatus.ColourOf(Enum.Parse<ComponentState>(mount.State)).ToString())} mount  {mount.Name} [{mount.Provider}]{(mount.ReadOnly ? " read-only" : "")}: {StateText(mount.State, mount.Text)}");
                foreach (var access in mount.AccessPaths)
                {
                    out_.WriteLine($"             {access.Label}: {access.Value}");
                }
            }
        }

        out_.WriteLine();
        var stats = snapshot.Stats;
        out_.WriteLine($"Files served: {stats.Total.FilesServed:N0} total, {stats.Session.FilesServed:N0} this session.  Bytes served: {Format.Bytes(stats.Total.BytesRead + stats.Total.BytesWritten)} total, {Format.Bytes(stats.Session.BytesRead + stats.Session.BytesWritten)} this session.  Active connections: {stats.ActiveConnections}.");
        return snapshot.AllGreen ? 0 : 1;
    }

    private static void PrintPlugins(TextWriter out_, IEnumerable<(string TypeId, string Kind, string DisplayName, string Origin, string? Error)> plugins)
    {
        out_.WriteLine("Plugins:");
        foreach (var p in plugins)
        {
            out_.WriteLine(p.Error is null
                ? $"  {p.Kind,-6} {p.TypeId,-10} {p.DisplayName}  ({p.Origin})"
                : $"  {p.Kind,-6} {p.TypeId,-10} {p.DisplayName}  ({p.Origin}): {p.Error}");
        }
    }

    public static string? ReadConfiguredLevel(SettingsPaths paths)
    {
        try
        {
            return JsonFiles.Read(paths.AppSettingsFile, CoreJsonContext.Default.AppSettings)?.LogLevel;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string StateText(string state, string text)
        => text.Length == 0 || text.Equals(state, StringComparison.OrdinalIgnoreCase) ? state : $"{state} - {text}";

    private static string Circle(string colour) => colour switch
    {
        nameof(StatusColour.Green) => "[OK]  ",
        nameof(StatusColour.Yellow) => "[..]  ",
        nameof(StatusColour.Red) => "[!!]  ",
        _ => "[--]  ",
    };
}
