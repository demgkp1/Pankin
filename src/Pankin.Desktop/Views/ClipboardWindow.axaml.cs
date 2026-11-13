using System;
using Avalonia.Controls;
using Avalonia.Input;
using Pankin.Desktop.ViewModels;

using Pankin.Desktop.Services;

namespace Pankin.Desktop.Views;

public partial class ClipboardWindow : Window
{
    private long _lastActivatedMs;

    public ClipboardWindow()
    {
        InitializeComponent();

        TopDragArea.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                BeginMoveDrag(e);
                e.Handled = true;
            }
        };

        KeyDown += OnWindowKeyDown;
        Activated += (_, _) => _lastActivatedMs = Environment.TickCount64;
        Opened += (_, _) =>
        {
            _lastActivatedMs = Environment.TickCount64;
            FocusSearchInput();
        };

        Deactivated += (_, _) =>
        {
            if (IsVisible &&
                Environment.TickCount64 - _lastActivatedMs > 180 &&
                DesktopAppContext.Instance.SettingsStore.Current.General.HideOnDeactivate)
            {
                PanelWindowBehavior.HideAfterDeactivation(this);
            }
        };

        HistoryListBox.DoubleTapped += (_, _) =>
        {
            if (DataContext is ClipboardViewModel vm)
            {
                vm.PasteSelectedCommand.Execute(null);
            }
        };
    }

    public void FocusSearchInput()
    {
        SearchInput.Focus();
        SearchInput.SelectAll();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ClipboardViewModel vm) return;

        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        bool isAlt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        // Alt + 1~9 快速粘贴对应条目
        if (isAlt)
        {
            int altNum = e.Key switch
            {
                Key.D1 or Key.NumPad1 => 1,
                Key.D2 or Key.NumPad2 => 2,
                Key.D3 or Key.NumPad3 => 3,
                Key.D4 or Key.NumPad4 => 4,
                Key.D5 or Key.NumPad5 => 5,
                Key.D6 or Key.NumPad6 => 6,
                Key.D7 or Key.NumPad7 => 7,
                Key.D8 or Key.NumPad8 => 8,
                Key.D9 or Key.NumPad9 => 9,
                _ => 0
            };

            if (altNum > 0)
            {
                vm.ExecuteIndexedPaste(altNum - 1);
                e.Handled = true;
                return;
            }
        }

        // Ctrl+P 固定/取消固定
        if (isCtrl && e.Key == Key.P)
        {
            vm.TogglePinCurrentCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Tab 交给启动器
        if (e.Key == Key.Tab)
        {
            vm.SendSelectedToLauncherCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Delete 删除当前记录
        if (e.Key == Key.Delete)
        {
            vm.DeleteCurrentCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Enter 粘贴 (Shift+Enter 纯文本)
        if (e.Key == Key.Enter)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                vm.PastePlainTextCommand.Execute(null);
            }
            else
            {
                vm.PasteSelectedCommand.Execute(null);
            }
            e.Handled = true;
            return;
        }

        // 上下箭头在历史列表中导航
        if (e.Key == Key.Down)
        {
            if (vm.Items.Count > 0)
            {
                vm.SelectedIndex = Math.Min(vm.Items.Count - 1, vm.SelectedIndex + 1);
                HistoryListBox.ScrollIntoView(vm.SelectedIndex);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Up)
        {
            if (vm.Items.Count > 0)
            {
                vm.SelectedIndex = Math.Max(0, vm.SelectedIndex - 1);
                HistoryListBox.ScrollIntoView(vm.SelectedIndex);
                e.Handled = true;
            }
        }
    }
}
