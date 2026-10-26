using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace AnyFS.App.Gui.Views;

/// <summary>
/// The windows' edges in dark mode (the user, 26-Oct-2026: "It's difficult to tell the boundaries of the windows"):
/// Windows 11 draws a dark window's one-pixel border nearly as dark as the window, so a dialog over the main window
/// melts into it. In dark mode every window's border, title bar and all, is drawn a clearer gray (DWMWA_BORDER_COLOR);
/// in light mode, and before Windows 11 (which has no such attribute), it is left as Windows draws it.
/// </summary>
internal static class WindowEdges
{
    /// <summary>DWMWA_BORDER_COLOR, Windows 11 (build 22000) on.</summary>
    private const int BorderColorAttribute = 34;

    /// <summary>DWMWA_COLOR_DEFAULT: the border as Windows draws it.</summary>
    private const uint DefaultColor = 0xFFFFFFFF;

    /// <summary>A COLORREF (0x00BBGGRR): a mid gray, clear on a dark window without standing out.</summary>
    private const uint DarkEdge = 0x006B6B6B;

    /// <summary>Gives every window opened from now on its edge, and keeps it with the theme.</summary>
    public static void FollowEveryWindow()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
            {
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                {
                    Follow(window);
                }
            }, RoutingStrategies.Direct);
        }
    }

    [SupportedOSPlatform("windows10.0.22000")]
    private static void Follow(Window window)
    {
        Apply(window);
        window.ActualThemeVariantChanged += (_, _) =>
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                Apply(window);
            }
        };
    }

    [SupportedOSPlatform("windows10.0.22000")]
    private static void Apply(Window window)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0)
        {
            return;
        }

        var color = window.ActualThemeVariant == ThemeVariant.Dark ? DarkEdge : DefaultColor;
        _ = DwmSetWindowAttribute(hwnd, BorderColorAttribute, ref color, sizeof(uint));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);
}
