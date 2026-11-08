using System;
using Avalonia.Controls;
using Avalonia.Input;
using Pankin.Desktop.ViewModels;

namespace Pankin.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        TopDragArea.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };

        KeyDown += OnWindowKeyDown;
        ResultListBox.DoubleTapped += (_, _) =>
        {
            if (DataContext is MainViewModel vm && vm.SelectedItem != null)
            {
                vm.SelectedItem.ExecuteCommand.Execute(null);
            }
        };
        Opened += (_, _) => FocusSearchInput();
    }

    public void FocusSearchInput()
    {
        SearchInput.Focus();
        SearchInput.SelectAll();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        bool isAlt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        // Alt+, 打开设置
        if (isAlt && e.Key == Key.OemComma)
        {
            vm.OpenSettingsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Ctrl+P 固定当前选中的应用
        if (isCtrl && e.Key == Key.P)
        {
            vm.TogglePinCurrentCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Ctrl+Enter 打开所在目录
        if (isCtrl && e.Key == Key.Enter)
        {
            vm.OpenContainingFolderCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Alt + 1~9 快速执行对应序号条目
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
                vm.ExecuteIndexedAction(altNum - 1);
                e.Handled = true;
                return;
            }
        }

        // 普通 Enter 执行默认选中动作
        if (e.Key == Key.Enter && !isAlt && !isCtrl)
        {
            vm.ExecuteDefaultCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // 上下箭头在搜索结果列表中导航
        if (!vm.IsSearchEmpty && vm.SearchResults.Count > 0)
        {
            if (e.Key == Key.Down)
            {
                vm.SelectedIndex = Math.Min(vm.SearchResults.Count - 1, vm.SelectedIndex + 1);
                ResultListBox.ScrollIntoView(vm.SelectedIndex);
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                vm.SelectedIndex = Math.Max(0, vm.SelectedIndex - 1);
                ResultListBox.ScrollIntoView(vm.SelectedIndex);
                e.Handled = true;
            }
        }
    }
}
