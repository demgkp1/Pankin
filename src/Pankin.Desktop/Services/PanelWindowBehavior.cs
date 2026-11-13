using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Pankin.Desktop.Services;

/// <summary>Transient panel dismissal, keeping screen pixels separate from logical UI units.</summary>
public static class PanelWindowBehavior
{
    // SharpHook uses X11 coordinates, which are not authoritative for native Wayland input.
    public static bool CanUseGlobalPointerCoordinates =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) &&
        !string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

    public static bool ContainsScreenPoint(Window window, double x, double y)
    {
        var topLeft = window.PointToScreen(default);
        var bottomRight = window.PointToScreen(new Point(window.Bounds.Width, window.Bounds.Height));
        return x >= topLeft.X && x < bottomRight.X && y >= topLeft.Y && y < bottomRight.Y;
    }

    public static void HideAfterDeactivation(Window window)
    {
        // Focus transitions (dragging, activation and popup interaction) can be temporary.
        // Recheck on the UI thread after the transition instead of hiding synchronously.
        DispatcherTimer.RunOnce(() =>
        {
            if (window.IsVisible && !window.IsActive && !window.IsPointerOver &&
                DesktopAppContext.Instance.SettingsStore.Current.General.HideOnDeactivate)
            {
                window.Hide();
            }
        }, TimeSpan.FromMilliseconds(120));
    }
}
