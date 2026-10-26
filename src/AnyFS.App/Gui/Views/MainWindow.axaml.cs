using System.Diagnostics;
using AnyFS.App.Gui.Services;
using AnyFS.App.Gui.ViewModels;
using AnyFS.Core.Model;
using AnyFS.Sdk;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace AnyFS.App.Gui.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// How long the window's thread may take to get to a probe before the wait is logged: a quarter second, as a background
    /// collection on a machine short of memory holds it up for less than one (26-Oct-2026).
    /// </summary>
    internal static readonly TimeSpan StallWorthLogging = TimeSpan.FromMilliseconds(250);

    /// <summary>At most one such line this often, so a window held up again and again does not fill the log.</summary>
    internal static readonly TimeSpan StallLoggedEvery = TimeSpan.FromSeconds(10);

    private readonly GuiHost _host;
    private readonly DispatcherTimer _timer;
    private readonly Timer _watchdog;
    private DateTime _lastRefresh = DateTime.MinValue;
    private int _dirty;
    private bool _closing;

    // when the probe in flight was sent (a Stopwatch timestamp), 0 when none is
    private long _probeSent;

    // when a wait was last logged (a Stopwatch timestamp), 0 before the first; the window's thread's own
    private long _stallLogged;

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
        _watchdog = new Timer(_ => Probe(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Opened += (_, _) =>
        {
            RestorePlacement();
            ViewModel.Refresh();
            _timer.Start();
            _watchdog.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        };
        Closing += OnClosing;
    }

    public MainViewModel ViewModel { get; }

    /// <summary>
    /// Every second, from outside, how long the window's thread takes to get to a probe: a long wait is logged with how much
    /// of it the garbage collector held every thread and how much of the machine's memory is in use, so a sluggish window
    /// says why (the user, 23-Oct-2026: "When enumerating at startup, the UI becomes less responsive"). A thread that waits
    /// for a background collection, as every thread allocating does once memory is short (26-Oct-2026), is not held by it:
    /// the memory says so instead.
    /// </summary>
    private void Probe()
    {
        var sent = Stopwatch.GetTimestamp();
        if (Interlocked.CompareExchange(ref _probeSent, sent, 0) != 0)
        {
            // the last probe is still waiting: it is being timed
            return;
        }

        var paused = GC.GetTotalPauseDuration();
        var full = GC.CollectionCount(2);
        Dispatcher.UIThread.Post(() =>
        {
            var waited = Stopwatch.GetElapsedTime(sent);
            Interlocked.Exchange(ref _probeSent, 0);
            if (waited >= StallWorthLogging && (_stallLogged == 0 || Stopwatch.GetElapsedTime(_stallLogged) >= StallLoggedEvery))
            {
                _stallLogged = Stopwatch.GetTimestamp();
                var memory = GC.GetGCMemoryInfo();
                _host.Logger.LogInformation("window: its thread was busy for {Milliseconds:F0} ms (the garbage collector held every thread for {Paused:F0} ms of it, {Full} full collections; {Heap:N0} MiB in use; the machine's memory {Load}% in use, which the collector takes to be high from {High}%)",
                    waited.TotalMilliseconds, (GC.GetTotalPauseDuration() - paused).TotalMilliseconds, GC.CollectionCount(2) - full, GC.GetTotalMemory(false) >> 20,
                    Percent(memory.MemoryLoadBytes, memory.TotalAvailableMemoryBytes), Percent(memory.HighMemoryLoadThresholdBytes, memory.TotalAvailableMemoryBytes));
            }
        }, DispatcherPriority.Input);
    }

    private static long Percent(long part, long whole) => whole > 0 ? (long)Math.Round(part * 100.0 / whole) : 0;

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
        _watchdog.Dispose();
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
