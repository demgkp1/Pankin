using System;
using Avalonia.Controls;
using Pankin.Desktop.ViewModels;

namespace Pankin.Desktop.Views;

public partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();

        TopDragArea.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };

        DataContextChanged += (_, _) =>
        {
            if (DataContext is SetupViewModel vm)
            {
                vm.CloseRequested += () => Close();
            }
        };
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
