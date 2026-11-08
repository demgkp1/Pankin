using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Pankin.Desktop.ViewModels;

namespace Pankin.Desktop.Views;

public partial class RenameWindow : Window
{
    public RenameWindow()
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
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    private async void OnAddFilesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not RenameViewModel vm) return;

        var storage = StorageProvider;
        if (!storage.CanOpen) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择待批量重命名的文件",
            AllowMultiple = true
        });

        if (files != null && files.Count > 0)
        {
            var paths = files.Select(f => f.Path.LocalPath).ToList();
            vm.AddFiles(paths);
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not RenameViewModel vm) return;

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        // Ctrl + Enter 执行
        if (isCtrl && e.Key == Key.Enter)
        {
            if (vm.CanApply)
            {
                vm.ApplyRenameCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }

        // Ctrl + Z 撤销
        if (isCtrl && e.Key == Key.Z)
        {
            if (vm.CanUndo)
            {
                vm.UndoCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }
    }
}
