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
            if (e.Key == Key.Escape)
            {
                Hide();
                e.Handled = true;
            }
        };
    }

    public void SelectTab(int index)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.SwitchTab(index);
        }
    }
}
