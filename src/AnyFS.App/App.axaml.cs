using AnyFS.App.Gui;
using AnyFS.App.Gui.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace AnyFS.App;

public partial class App : Application
{
    /// <summary>Set by Program before Avalonia starts; null in the headless tests, which build their own host.</summary>
    public static GuiOptions? Options { get; set; }

    public GuiHost? Host { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Host = GuiHost.Create(Options ?? new GuiOptions());
            PluginIcons.Registry = Host.Engine.Registry;
            ApplyTheme(Host.Engine.Settings.App.Theme);
            WindowEdges.FollowEveryWindow();
            var window = new MainWindow(Host);
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            Host.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>app.json's theme: system (default), light or dark (spec 9.2).</summary>
    public void ApplyTheme(string? theme)
    {
        RequestedThemeVariant = theme?.Trim().ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
