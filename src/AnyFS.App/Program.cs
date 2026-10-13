using System.CommandLine;
using AnyFS.App.Cli;
using AnyFS.App.Gui;
using Avalonia;

namespace AnyFS.App;

// anyfs                                  the window
// anyfs serve [--collection NAME]...     run all (or the named) enabled collections until Ctrl+C / SIGTERM
// anyfs status                           print collections, sources, mounts, states, access paths; exit 0 if all green
// anyfs --settings-dir DIR ...           any command (or the window), against another settings folder
// anyfs --log-level LEVEL ...            trace|debug|info|warn|error
// anyfs --version
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && OperatingSystem.IsWindows())
        {
            // a WinExe has no console; with arguments, use the one we were started from (spec 12)
            ConsoleAttach.AttachToParent();
        }

        var root = Commands.BuildRootCommand(settingsDirOption: out var settingsDir, logLevelOption: out var logLevel);
        root.SetAction(parseResult => RunWindow(parseResult.GetValue(settingsDir), parseResult.GetValue(logLevel)));

        var parse = root.Parse(args);
        var configuration = new InvocationConfiguration { ProcessTerminationTimeout = TimeSpan.FromSeconds(20) };
        if (parse.Action is not null && ReferenceEquals(parse.Action, root.Action))
        {
            // the window must run on the main thread (macOS insists); everything else can be async
            return parse.Invoke(configuration);
        }

        return parse.InvokeAsync(configuration).GetAwaiter().GetResult();
    }

    private static int RunWindow(string? settingsDir, string? logLevel)
    {
        App.Options = new GuiOptions(settingsDir, logLevel);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
    }

    /// <summary>Avalonia configuration; also used by the XAML previewer and the headless tests.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
