using System.Diagnostics;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.App.Gui.Services;

/// <summary>
/// Opens access paths with the OS: a path in the file manager, a URL in the browser (or the file
/// manager for ftp:// and dav://), a command first when a provider needs one (the device-less
/// <c>net use</c> for SMB). Never maps a drive (spec 8.9).
/// </summary>
public sealed class ShellOpener(ILogger logger, Func<string, string, Task>? showText = null) : IOpener
{
    public void Open(MountOpenAction action, string? subPath)
    {
        try
        {
            switch (action)
            {
                case MountOpenAction.FileManager fm:
                    Launch(AppendPath(fm.Path, subPath));
                    break;
                case MountOpenAction.Browser browser:
                    Launch(AppendUrl(browser.Url, subPath));
                    break;
                case MountOpenAction.Command command:
                    if (command.Reset is { } reset)
                    {
                        // best-effort: drop a cached connection to the same server so the real command reconnects with the
                        // current credentials (after a password change Windows keeps the old ones). Its failure is expected
                        // when there is nothing to drop, and a plain /delete declines rather than forcing open files shut.
                        RunCommand(command.FileName, reset, command.Elevated);
                    }

                    if (!RunCommand(command.FileName, command.Arguments, command.Elevated))
                    {
                        logger.LogWarning("the command before opening {Target} did not succeed: {Command}", command.ThenOpen, command.FileName);
                    }

                    if (command.ThenOpen is { } then)
                    {
                        Launch(AppendPath(then, subPath));
                    }

                    break;
                case MountOpenAction.ShowText text:
                    showText?.Invoke(text.Title, text.Text);
                    break;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            logger.LogWarning(ex, "could not open {Action}", action);
        }
    }

    public void OpenFile(string path) => Launch(path);

    /// <summary><c>&lt;mount root&gt;\&lt;source path&gt;</c> in the OS's separator.</summary>
    public static string AppendPath(string root, string? subPath)
    {
        if (string.IsNullOrEmpty(subPath))
        {
            return root;
        }

        var tail = subPath.Replace('/', Path.DirectorySeparatorChar);
        return root.EndsWith(Path.DirectorySeparatorChar) ? root + tail : root + Path.DirectorySeparatorChar + tail;
    }

    public static string AppendUrl(string url, string? subPath)
    {
        if (string.IsNullOrEmpty(subPath))
        {
            return url;
        }

        var segments = subPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString);
        return url.TrimEnd('/') + "/" + string.Join("/", segments) + "/";
    }

    private static void Launch(string target)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo("open", [target]) { UseShellExecute = false })?.Dispose();
        }
        else
        {
            Process.Start(new ProcessStartInfo("xdg-open", [target]) { UseShellExecute = false })?.Dispose();
        }
    }

    /// <summary>Runs the command and waits for it (up to 15 s); elevated commands go through the shell's "run as administrator" on Windows (a UAC prompt).</summary>
    private static bool RunCommand(string fileName, IReadOnlyList<string> arguments, bool elevated)
    {
        var shell = elevated && OperatingSystem.IsWindows();
        var info = new ProcessStartInfo(fileName) { UseShellExecute = shell, CreateNoWindow = true, RedirectStandardInput = !shell };
        if (shell)
        {
            info.Verb = "runas";
            info.WindowStyle = ProcessWindowStyle.Hidden;
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info);
        if (process is null)
        {
            return false;
        }

        // close stdin so a command that would prompt (net use /delete on a share with open files) reads end-of-file and
        // declines, instead of inheriting the console and hanging
        if (!shell)
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
        }

        return process.WaitForExit(15_000) && process.ExitCode == 0;
    }
}
