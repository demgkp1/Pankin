using System;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pankin.Core.Clipboard;
using Pankin.Desktop.Services;

namespace Pankin.Desktop.ViewModels;

public sealed partial class ClipboardCategoryTab : ObservableObject
{
    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;

    public IRelayCommand SelectCommand { get; }

    public ClipboardCategoryTab(string name, bool isSelected, Action onSelect)
    {
        Name = name;
        _isSelected = isSelected;
        SelectCommand = new RelayCommand(onSelect);
    }
}

public sealed partial class ClipboardItemViewModel : ObservableObject
{
    public string Id { get; }
    public ClipKind Kind { get; }
    public string? FullText { get; }
    public string PreviewText { get; }
    public string TimeAgo { get; }
    public string KindName { get; }
    public string TagColor { get; }
    public string HotkeyText { get; }

    public string PinStrokeColor => Pinned ? "#F59E0B" : "#80FFFFFF";
    public string SubtitleInfo => Pinned ? $"已固定 · {TimeAgo}" : TimeAgo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinStrokeColor))]
    [NotifyPropertyChangedFor(nameof(SubtitleInfo))]
    private bool _pinned;

    public IRelayCommand PasteCommand { get; }
    public IRelayCommand TogglePinCommand { get; }
    public IRelayCommand DeleteCommand { get; }

    public ClipboardItemViewModel(
        ClipboardEntry entry,
        string hotkeyText,
        Action onPaste,
        Action onTogglePin,
        Action onDelete)
    {
        Id = entry.Id;
        Kind = entry.Kind;
        FullText = entry.Text;
        Pinned = entry.Pinned;
        HotkeyText = hotkeyText;

        PasteCommand = new RelayCommand(onPaste);
        TogglePinCommand = new RelayCommand(onTogglePin);
        DeleteCommand = new RelayCommand(onDelete);

        var text = entry.Text ?? string.Empty;
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        PreviewText = string.Join("\n", lines.Take(2));
        if (lines.Length > 2) PreviewText += "...";

        TimeAgo = FormatTimeAgo(entry.CopiedAt);
        KindName = entry.Kind switch
        {
            ClipKind.Code => "<>",
            ClipKind.Link => "🔗",
            ClipKind.Color => "🎨",
            ClipKind.Image => "🖼",
            ClipKind.Files => "📁",
            _ => "T"
        };
        TagColor = entry.Kind switch
        {
            ClipKind.Code => "#8B5CF6",
            ClipKind.Link => "#06B6D4",
            ClipKind.Color => "#EC4899",
            ClipKind.Image => "#10B981",
            ClipKind.Files => "#F59E0B",
            _ => "#3B82F6"
        };
    }

    private static string FormatTimeAgo(DateTimeOffset time)
    {
        var diff = DateTimeOffset.UtcNow - time;
        if (diff.TotalSeconds < 60) return "刚刚";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} 分钟前";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} 小时前";
        if (diff.TotalDays < 30) return $"{(int)diff.TotalDays} 天前";
        return time.LocalDateTime.ToString("MM-dd HH:mm");
    }
}

public partial class ClipboardViewModel : ViewModelBase, IDisposable
{
    private readonly DesktopAppContext _context;

    [ObservableProperty]
    private string _searchKeyword = string.Empty;

    [ObservableProperty]
    private string _selectedCategory = "全部";

    [ObservableProperty]
    private ClipboardItemViewModel? _selectedItem;

    [ObservableProperty]
    private int _selectedIndex = -1;

    public ObservableCollection<ClipboardCategoryTab> Categories { get; } = [];
    public ObservableCollection<ClipboardItemViewModel> Items { get; } = [];

    public event Action? RequestHideWindow;
    public event Action<string>? RequestSendToLauncher;

    public ClipboardViewModel() : this(DesktopAppContext.Instance)
    {
    }

    public ClipboardViewModel(DesktopAppContext context)
    {
        _context = context;
        _context.ClipboardHistory.HistoryChanged += OnHistoryChanged;

        InitCategories();
        ReloadItems();
    }

    private void InitCategories()
    {
        Categories.Clear();
        string[] cats = ["全部", "文本", "代码", "链接", "颜色", "已固定"];
        foreach (var cat in cats)
        {
            var tab = new ClipboardCategoryTab(cat, cat == SelectedCategory, () =>
            {
                SelectedCategory = cat;
                foreach (var c in Categories)
                {
                    c.IsSelected = c.Name == cat;
                }
                ReloadItems();
            });
            Categories.Add(tab);
        }
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(ReloadItems);
    }

    partial void OnSearchKeywordChanged(string value) => ReloadItems();

    public void ReloadItems()
    {
        Items.Clear();

        bool? filterPinned = SelectedCategory == "已固定" ? true : null;
        ClipKind? filterKind = SelectedCategory switch
        {
            "文本" => ClipKind.Text,
            "代码" => ClipKind.Code,
            "链接" => ClipKind.Link,
            "颜色" => ClipKind.Color,
            _ => null
        };

        int limit = Math.Clamp(_context.SettingsStore.Current.Clipboard.HistoryLimit, 10, 2000);
        var results = _context.ClipboardStorage.QueryEntries(
            limit: limit,
            search: string.IsNullOrWhiteSpace(SearchKeyword) ? null : SearchKeyword.Trim(),
            kind: filterKind,
            pinned: filterPinned);

        int index = 1;
        foreach (var entry in results)
        {
            string hotkey = index <= 9 ? $"Alt {index}" : string.Empty;

            var itemVm = new ClipboardItemViewModel(
                entry,
                hotkey,
                onPaste: () => _ = PasteEntryTextAsync(entry.Text),
                onTogglePin: () =>
                {
                    _context.ClipboardStorage.SetPinned(entry.Id, !entry.Pinned);
                    ReloadItems();
                },
                onDelete: () =>
                {
                    _context.ClipboardStorage.Delete(entry.Id);
                    ReloadItems();
                });

            Items.Add(itemVm);
            index++;
        }

        if (Items.Count > 0)
        {
            SelectedIndex = 0;
            SelectedItem = Items[0];
        }
    }

    public async Task PasteEntryTextAsync(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            await _context.ClipboardService.SetTextAsync(text);
            RequestHideWindow?.Invoke();
        }
    }

    public void ExecuteIndexedPaste(int index)
    {
        if (index >= 0 && index < Items.Count)
        {
            Items[index].PasteCommand.Execute(null);
        }
    }

    [RelayCommand]
    public void TogglePinCurrent()
    {
        if (SelectedItem != null)
        {
            SelectedItem.TogglePinCommand.Execute(null);
        }
    }

    [RelayCommand]
    public void DeleteCurrent()
    {
        if (SelectedItem != null)
        {
            SelectedItem.DeleteCommand.Execute(null);
        }
    }

    [RelayCommand]
    public void ClearUnpinned()
    {
        _context.ClipboardStorage.ClearUnpinned();
        ReloadItems();
    }

    [RelayCommand]
    public async Task PasteSelectedAsync()
    {
        if (SelectedItem != null)
        {
            await PasteEntryTextAsync(SelectedItem.FullText);
        }
    }

    [RelayCommand]
    public async Task PastePlainTextAsync()
    {
        if (SelectedItem?.FullText != null)
        {
            var cleaned = CleanPlainText(SelectedItem.FullText);
            await _context.ClipboardService.SetTextAsync(cleaned);
            RequestHideWindow?.Invoke();
        }
    }

    [RelayCommand]
    public void SendSelectedToLauncher()
    {
        if (!string.IsNullOrEmpty(SelectedItem?.FullText))
        {
            RequestHideWindow?.Invoke();
            RequestSendToLauncher?.Invoke(SelectedItem.FullText);
        }
    }

    private static string CleanPlainText(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var normalized = input.Replace("\r\n", "\n").Replace('\r', '\n');
        return Regex.Replace(normalized.Trim(), @"\n{3,}", "\n\n");
    }

    public void Dispose()
    {
        _context.ClipboardHistory.HistoryChanged -= OnHistoryChanged;
    }
}
