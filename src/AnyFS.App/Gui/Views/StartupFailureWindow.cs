using AnyFS.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;

namespace AnyFS.App.Gui.Views;

/// <summary>
/// Shown in the window's place when AnyFS cannot start: another AnyFS has these settings, or they cannot be read (a file
/// held by another program). No window came before, and nothing said why: the reason was in the log, when the log had
/// been opened by then (the review of 21-Nov-2026, R7f and R7g). Closing it ends the process with <see cref="ExitCode"/>.
/// </summary>
public sealed class StartupFailureWindow : Window
{
    public StartupFailureWindow(Exception failure, GuiOptions? options)
    {
        Message = Explain(failure, options);
        ExitCode = failure is SettingsInUseException ? 3 : 1;
        Title = "AnyFS";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 20,
            Children =
            {
                new SelectableTextBlock { Text = Message, TextWrapping = TextWrapping.Wrap },
                close,
            },
        };
        Closed += (_, _) => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(ExitCode);
    }

    public string Message { get; }

    /// <summary>3 when another AnyFS has these settings (as <c>anyfs serve</c> says it), 1 for anything else.</summary>
    public int ExitCode { get; }

    /// <summary>What kept AnyFS from starting, for the person who started it, with where the log is when it may say more.</summary>
    public static string Explain(Exception failure, GuiOptions? options)
    {
        if (failure is SettingsInUseException)
        {
            return failure.Message;
        }

        var text = $"AnyFS could not start.\n\n{failure.GetBaseException().Message}";
        try
        {
            text += $"\n\nThe log may say more: {SettingsPaths.Resolve(options?.SettingsDir).LogFile}";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            // a settings folder that is no path: the failure above says so
        }

        return text;
    }
}
