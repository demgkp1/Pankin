using Avalonia.Controls;
using Avalonia.Input;
using Pankin.Desktop.ViewModels;

namespace Pankin.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        LeftNavBorder.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };

        CloseButton.Click += (_, _) => Hide();

        KeyDown += (_, e) =>
        {
            if (DataContext is SettingsViewModel vm && !string.IsNullOrEmpty(vm.RecordingTarget))
            {
                if (e.Key == Key.Escape || e.Key == Key.Back)
                {
                    vm.CancelRecording();
                    e.Handled = true;
                    return;
                }

                // 忽略纯修饰键自身按下
                if (e.Key is Key.LeftCtrl or Key.RightCtrl or
                             Key.LeftAlt or Key.RightAlt or
                             Key.LeftShift or Key.RightShift or
                             Key.LWin or Key.RWin)
                {
                    return;
                }

                var keyStr = FormatKeyCombination(e);
                if (!string.IsNullOrEmpty(keyStr))
                {
                    vm.SetRecordedHotkey(keyStr);
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Escape)
            {
                Hide();
                e.Handled = true;
            }
        };
    }

    private static string FormatKeyCombination(KeyEventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) sb.Append("Ctrl+");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) sb.Append("Alt+");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) sb.Append("Shift+");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) sb.Append("Super+");

        string keyPart = e.Key switch
        {
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.Oem1 => ";",
            Key.Oem2 => "/",
            Key.Oem3 => "`",
            Key.Oem4 => "[",
            Key.Oem5 => "\\",
            Key.Oem6 => "]",
            Key.Oem7 => "'",
            _ => e.Key.ToString()
        };

        sb.Append(keyPart);
        return sb.ToString();
    }

    public void SelectTab(int index)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.SwitchTab(index);
        }
    }
}
