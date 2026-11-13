using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Pankin.Desktop.Services;

/// <summary>Transient panel dismissal, keeping screen pixels separate from logical UI units.</summary>
public static class PanelWindowBehavior
{
    private static readonly ConditionalWeakTable<Window, DeactivationState> DeactivationStates = new();

    // SharpHook uses X11 coordinates, which are not authoritative for Wayland input.
    public static bool CanUseGlobalPointerCoordinates =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) &&
        !string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

    public static bool ContainsScreenPoint(WindowBase window, double x, double y)
    {
        var topLeft = window.PointToScreen(default);
        var bottomRight = window.PointToScreen(new Point(window.Bounds.Width, window.Bounds.Height));
        return x >= topLeft.X && x < bottomRight.X && y >= topLeft.Y && y < bottomRight.Y;
    }

    public static bool ContainsScreenPointWithPopups(Window window, double x, double y) =>
        ContainsScreenPoint(window, x, y) || OwnedPopupRoots(window).Any(popup => ContainsScreenPoint(popup, x, y));

    public static void HideAfterDeactivation(Window window, Action? beforeHide = null) =>
        DeactivationStates.GetValue(window, static w => new DeactivationState(w)).Schedule(beforeHide);

    private static IEnumerable<WindowBase> OwnedPopupRoots(Window window)
    {
        foreach (var control in window.GetVisualDescendants().OfType<Control>().Prepend(window))
        {
            if (control.ContextMenu is { IsOpen: true } menu && TopLevel.GetTopLevel(menu) is WindowBase root)
                yield return root;
        }

        foreach (var popup in window.GetLogicalDescendants().OfType<Popup>())
        {
            if (popup.IsOpen && popup.Child is { } child && TopLevel.GetTopLevel(child) is WindowBase root)
                yield return root;
        }
    }

    private sealed class DeactivationState
    {
        private readonly Window _window;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
        private Action? _beforeHide;

        public DeactivationState(Window window)
        {
            _window = window;
            _timer.Tick += OnTick;
            window.Activated += OnActivated;
            window.PropertyChanged += OnPropertyChanged;
            window.Closed += OnClosed;
        }

        public void Schedule(Action? beforeHide)
        {
            Cancel();
            if (!_window.IsVisible || !HideEnabled) return;
            _beforeHide = beforeHide;
            _timer.Start();
        }

        private static bool HideEnabled => DesktopAppContext.Instance.SettingsStore.Current.General.HideOnDeactivate;

        private void OnTick(object? sender, EventArgs e)
        {
            _timer.Stop();
            if (!_window.IsVisible || _window.IsActive || !HideEnabled)
            {
                Cancel();
                return;
            }

            // Hover is not keyboard focus: Alt+Tab can leave the mouse over an inactive panel.
            // Only an active owned dialog/popup may keep a deactivated panel visible. Recheck
            // while that popup owns focus so closing it or switching away cannot strand a panel.
            if (_window.OwnedWindows.Any(w => w.IsVisible && w.IsActive) ||
                OwnedPopupRoots(_window).Any(popup => popup.IsActive))
            {
                _timer.Start();
                return;
            }

            var beforeHide = _beforeHide;
            Cancel();
            beforeHide?.Invoke();
            _window.Hide();
        }

        private void Cancel()
        {
            _timer.Stop();
            _beforeHide = null;
        }

        private void OnActivated(object? sender, EventArgs e) => Cancel();

        private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.IsVisibleProperty && !_window.IsVisible) Cancel();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            Cancel();
            _timer.Tick -= OnTick;
            _window.Activated -= OnActivated;
            _window.PropertyChanged -= OnPropertyChanged;
            _window.Closed -= OnClosed;
            DeactivationStates.Remove(_window);
        }
    }
}
