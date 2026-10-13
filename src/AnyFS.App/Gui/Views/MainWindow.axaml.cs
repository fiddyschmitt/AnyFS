using AnyFS.App.Gui.Services;
using AnyFS.App.Gui.ViewModels;
using AnyFS.Core.Model;
using AnyFS.Sdk;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AnyFS.App.Gui.Views;

public partial class MainWindow : Window
{
    private readonly GuiHost _host;
    private readonly DispatcherTimer _timer;
    private DateTime _lastRefresh = DateTime.MinValue;
    private int _dirty;
    private bool _closing;

    public MainWindow(GuiHost host)
    {
        _host = host;
        InitializeComponent();
        var clipboard = new AvaloniaClipboard(this);
        var dialogs = new AvaloniaDialogs(this, clipboard);
        var opener = new ShellOpener(host.Logger, (title, text) => Dispatcher.UIThread.InvokeAsync(() => dialogs.ShowTextAsync(title, text)));
        ViewModel = new MainViewModel(host.Engine, dialogs, opener, clipboard);
        DataContext = ViewModel;
        host.Prompts.SetPresenter(ShowPromptAsync);
        host.Engine.Changed += OnEngineChanged;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        Opened += (_, _) =>
        {
            RestorePlacement();
            ViewModel.Refresh();
            _timer.Start();
        };
        Closing += OnClosing;
    }

    public MainViewModel ViewModel { get; }

    /// <summary>Changes arrive on any thread; the UI picks them up within a quarter second, and every second regardless.</summary>
    private void OnEngineChanged() => Interlocked.Exchange(ref _dirty, 1);

    private void Tick()
    {
        var now = DateTime.UtcNow;
        if (Interlocked.Exchange(ref _dirty, 0) == 1 || now - _lastRefresh >= TimeSpan.FromSeconds(1))
        {
            _lastRefresh = now;
            ViewModel.Refresh();
        }
    }

    private Task<PromptResult> ShowPromptAsync(PromptDialogViewModel prompt, CancellationToken ct)
        => Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new PromptDialog(prompt);
            using var registration = ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(PromptResult.Cancelled)));
            return await dialog.ShowDialog<PromptResult?>(this) ?? PromptResult.Cancelled;
        });

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _timer.Stop();
        _host.Engine.Changed -= OnEngineChanged;
        SavePlacement();
        if (_host.IsShutDown)
        {
            // already stopped (the tests do this): let the close go through
            return;
        }

        // closing the window exits (spec 11): stop everything first, then really close
        e.Cancel = true;
        try
        {
            await _host.ShutdownAsync();
        }
        finally
        {
            Close();
        }
    }

    private void RestorePlacement()
    {
        var placement = _host.Engine.Settings.App.Window;
        if (placement is not { Width: >= 400, Height: >= 300 })
        {
            return;
        }

        var position = new PixelPoint(placement.X, placement.Y);
        if (Screens.ScreenFromPoint(position) is not null)
        {
            Position = position;
        }

        Width = placement.Width;
        Height = placement.Height;
        if (placement.Maximised)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SavePlacement()
    {
        try
        {
            var app = _host.Engine.Settings.App;
            var maximised = WindowState == WindowState.Maximized;
            var previous = app.Window;
            app.Window = new WindowPlacement
            {
                X = maximised && previous is not null ? previous.X : Position.X,
                Y = maximised && previous is not null ? previous.Y : Position.Y,
                Width = maximised && previous is not null ? previous.Width : (int)Width,
                Height = maximised && previous is not null ? previous.Height : (int)Height,
                Maximised = maximised,
            };
            _host.Engine.Settings.SaveApp();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
