using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pankin.Core.Abstractions;
using Pankin.Core.Launcher;
using Pankin.Core.Localization;
using Pankin.Desktop.Services;

namespace Pankin.Desktop.ViewModels;

public sealed class AppTileItem : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    public string? Subtitle { get; }
    public string BadgeColor { get; }
    public string Glyph { get; }
    public Bitmap? IconBitmap { get; }
    public string? IconPathData { get; }
    public StreamGeometry? IconGeometry => IconPathData != null ? LucideIcons.GetGeometry(IconPathData) : null;
    public bool HasIconPath => IconPathData != null;
    public bool ShowIconPath => IconBitmap == null && HasIconPath;
    public bool ShowGlyph => IconBitmap == null && !HasIconPath;
    public bool IsPinned { get; }
    public string PinMenuHeader => IsPinned ? "取消固定" : "固定到启动面板";
    public bool CanOpenLocation { get; }
    public bool CanRunAsAdmin { get; }

    public IRelayCommand LaunchCommand { get; }
    public IRelayCommand ExecuteCommand => LaunchCommand;
    public IRelayCommand? TogglePinCommand { get; }
    public IRelayCommand? OpenLocationCommand { get; }
    public IRelayCommand? RunAsAdminCommand { get; }

    public AppTileItem(
        string id,
        string name,
        string? subtitle,
        string badgeColor,
        string glyph,
        Bitmap? iconBitmap,
        Action onLaunch,
        string? iconPathData = null,
        bool isPinned = false,
        bool canOpenLocation = false,
        bool canRunAsAdmin = false,
        Action? onTogglePin = null,
        Action? onOpenLocation = null,
        Action? onRunAsAdmin = null)
    {
        Id = id;
        Name = name;
        Subtitle = subtitle;
        BadgeColor = badgeColor;
        Glyph = glyph;
        IconBitmap = iconBitmap;
        LaunchCommand = new RelayCommand(onLaunch);
        IconPathData = iconPathData;
        IsPinned = isPinned;
        CanOpenLocation = canOpenLocation;
        CanRunAsAdmin = canRunAsAdmin;

        if (onTogglePin != null)
        {
            TogglePinCommand = new RelayCommand(onTogglePin);
        }
        if (onOpenLocation != null)
        {
            OpenLocationCommand = new RelayCommand(onOpenLocation);
        }
        if (onRunAsAdmin != null)
        {
            RunAsAdminCommand = new RelayCommand(onRunAsAdmin);
        }
    }
}

public sealed class QuickActionTile : ObservableObject
{
    public string Title { get; }
    public string IconPathData { get; }
    public StreamGeometry IconGeometry => LucideIcons.GetGeometry(IconPathData);
    public IRelayCommand ExecuteCommand { get; }

    public QuickActionTile(string title, string iconPathData, Action onExecute)
    {
        Title = title;
        IconPathData = iconPathData;
        ExecuteCommand = new RelayCommand(onExecute);
    }
}

public sealed class LauncherListItem : ObservableObject
{
    private readonly ILauncherSearch _search;
    private readonly ILauncherActionHost _actionHost;
    private readonly Action? _onHide;
    private readonly Action<string>? _onTogglePin;
    private readonly Func<string, bool>? _isPinned;

    public LauncherItem CoreItem { get; }
    public string Title => CoreItem.Title;
    public string Subtitle => CoreItem.Subtitle ?? string.Empty;
    public string Tag { get; }
    public string TagColor { get; }
    public string HotkeyText { get; set; }
    public LauncherItemKind Kind => CoreItem.Kind;
    public object? Payload => CoreItem.Tag ?? CoreItem.Subtitle ?? CoreItem.Title;

    public string Group => CoreItem.Group;
    public string GroupDisplayName { get; set; }
    public bool IsFirstInGroup { get; set; }

    public Bitmap? IconBitmap { get; }
    public string? IconPathData { get; }
    public StreamGeometry? IconGeometry => IconPathData != null ? LucideIcons.GetGeometry(IconPathData) : null;
    public bool HasIconPath => IconPathData != null;
    public bool ShowIconPath => IconBitmap == null && HasIconPath;
    public string Glyph { get; }
    public bool ShowGlyph => IconBitmap == null && !HasIconPath;
    public string BadgeColor { get; }

    public bool CanOpenLocation => Kind is LauncherItemKind.App or LauncherItemKind.File or LauncherItemKind.Folder;
    public bool CanOpenInTerminal => Kind is LauncherItemKind.App or LauncherItemKind.File or LauncherItemKind.Folder or LauncherItemKind.Command;
    public bool CanRunAsAdmin => Kind is LauncherItemKind.App or LauncherItemKind.Command;

    public bool CanPin => Kind == LauncherItemKind.App && ((object)CoreItem is AppEntry || CoreItem.Tag is AppEntry || !string.IsNullOrEmpty(CoreItem.Id));
    public string AppId => (CoreItem.Tag as AppEntry)?.Id ?? CoreItem.Id;
    public bool IsPinned => _isPinned?.Invoke(AppId) ?? false;
    public string PinMenuHeader => IsPinned ? "取消固定" : "固定到启动面板";

    public IAsyncRelayCommand ExecuteCommand { get; }
    public IAsyncRelayCommand OpenLocationCommand { get; }
    public IAsyncRelayCommand CopyPathCommand { get; }
    public IAsyncRelayCommand OpenInTerminalCommand { get; }
    public IAsyncRelayCommand RunAsAdminCommand { get; }
    public IRelayCommand? TogglePinCommand { get; }

    public LauncherListItem(
        LauncherItem coreItem,
        string tag,
        string tagColor,
        string hotkeyText,
        string groupDisplayName,
        bool isFirstInGroup,
        ILauncherSearch search,
        ILauncherActionHost actionHost,
        Action? onHide,
        Action<string>? onTogglePin = null,
        Func<string, bool>? isPinned = null)
    {
        CoreItem = coreItem;
        Tag = tag;
        TagColor = tagColor;
        HotkeyText = hotkeyText;
        GroupDisplayName = groupDisplayName;
        IsFirstInGroup = isFirstInGroup;
        _search = search;
        _actionHost = actionHost;
        _onHide = onHide;
        _onTogglePin = onTogglePin;
        _isPinned = isPinned;

        (IconBitmap, IconPathData, Glyph, BadgeColor) = ResolveIcon(coreItem);

        if (onTogglePin != null)
        {
            TogglePinCommand = new RelayCommand(() =>
            {
                _onTogglePin?.Invoke(AppId);
                OnPropertyChanged(nameof(IsPinned));
                OnPropertyChanged(nameof(PinMenuHeader));
            });
        }

        ExecuteCommand = new AsyncRelayCommand(async () =>
        {
            _onHide?.Invoke();
            await _search.ExecuteAsync(CoreItem);
        });

        OpenLocationCommand = new AsyncRelayCommand(async () =>
        {
            _onHide?.Invoke();
            if (_search is ILauncherItemActions actions)
            {
                await actions.RunAsync(CoreItem, LauncherItemAction.OpenLocation);
            }
            else
            {
                var target = GetLocationTarget(CoreItem);
                if (!string.IsNullOrEmpty(target))
                {
                    await _actionHost.RevealAsync(target);
                }
            }
        });

        CopyPathCommand = new AsyncRelayCommand(async () =>
        {
            _onHide?.Invoke();
            if (_search is ILauncherItemActions actions)
            {
                await actions.RunAsync(CoreItem, LauncherItemAction.CopyPath);
            }
            else
            {
                var text = CoreItem.Subtitle ?? CoreItem.Title;
                await _actionHost.CopyTextAsync(text);
            }
        });

        OpenInTerminalCommand = new AsyncRelayCommand(async () =>
        {
            _onHide?.Invoke();
            if (CoreItem.Kind == LauncherItemKind.Command)
            {
                await _actionHost.RunInTerminalAsync(CoreItem.Title, false);
            }
            else
            {
                var workDir = GetWorkingDirectory(CoreItem);
                await _actionHost.RunInTerminalAsync("bash", false, workDir);
            }
        });

        RunAsAdminCommand = new AsyncRelayCommand(async () =>
        {
            _onHide?.Invoke();
            await _search.ExecuteElevatedAsync(CoreItem);
        });
    }

    private static (Bitmap? IconBitmap, string? IconPathData, string Glyph, string BadgeColor) ResolveIcon(LauncherItem item)
    {
        var badgeColor = LinuxIconHelper.GetGradientColor(item.Title);
        var glyph = !string.IsNullOrWhiteSpace(item.Icon?.Glyph) && item.Icon.Glyph.Length <= 2
            ? item.Icon.Glyph
            : LinuxIconHelper.GetGlyphChar(item.Title);

        Bitmap? bitmap = null;
        if (!string.IsNullOrWhiteSpace(item.Icon?.ImagePath))
        {
            bitmap = LinuxIconHelper.TryLoadIcon(item.Icon.ImagePath);
        }
        if (bitmap == null && item.Tag is AppEntry app && !string.IsNullOrWhiteSpace(app.Icon))
        {
            bitmap = LinuxIconHelper.TryLoadIcon(app.Icon);
        }

        string? iconPath = null;
        if (bitmap == null)
        {
            if (item.Group == LauncherGroups.Timer)
            {
                iconPath = LucideIcons.Timer;
            }
            else
            {
                iconPath = item.Kind switch
                {
                    LauncherItemKind.Calculation => LucideIcons.Calc,
                    LauncherItemKind.Folder => LucideIcons.Folder,
                    LauncherItemKind.File => LucideIcons.Doc,
                    LauncherItemKind.Command => LucideIcons.Terminal,
                    LauncherItemKind.Web => LucideIcons.Search,
                    LauncherItemKind.Ai => LucideIcons.Ai,
                    LauncherItemKind.Action => LucideIcons.Tools,
                    LauncherItemKind.App => LucideIcons.Grid,
                    _ => null
                };
            }
        }

        return (bitmap, iconPath, glyph, badgeColor);
    }

    private static string? GetLocationTarget(LauncherItem item)
    {
        if (item.Kind == LauncherItemKind.App)
        {
            if (item.Tag is AppEntry app && !string.IsNullOrWhiteSpace(app.DesktopFilePath))
            {
                return app.DesktopFilePath;
            }
            return item.Subtitle ?? item.Title;
        }

        if (item.Kind is LauncherItemKind.File or LauncherItemKind.Folder)
        {
            return item.Id.StartsWith("file:", StringComparison.Ordinal)
                ? item.Id[5..]
                : item.Subtitle ?? item.Title;
        }

        return null;
    }

    private static string? GetWorkingDirectory(LauncherItem item)
    {
        if (item.Kind == LauncherItemKind.Folder)
        {
            return item.Id.StartsWith("file:", StringComparison.Ordinal)
                ? item.Id[5..]
                : item.Subtitle ?? item.Title;
        }

        if (item.Kind == LauncherItemKind.File)
        {
            var path = item.Id.StartsWith("file:", StringComparison.Ordinal)
                ? item.Id[5..]
                : item.Subtitle ?? item.Title;
            return Path.GetDirectoryName(path);
        }

        if (item.Kind == LauncherItemKind.App && item.Tag is AppEntry app)
        {
            if (!string.IsNullOrWhiteSpace(app.WorkingDirectory) && Directory.Exists(app.WorkingDirectory))
            {
                return app.WorkingDirectory;
            }
            if (!string.IsNullOrWhiteSpace(app.DesktopFilePath))
            {
                return Path.GetDirectoryName(app.DesktopFilePath);
            }
        }

        return null;
    }
}

public partial class MainViewModel : ViewModelBase
{
    private readonly DesktopAppContext _context;
    private CancellationTokenSource? _searchCts;
    private int _searchVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchEmpty))]
    [NotifyPropertyChangedFor(nameof(QuickActionTitle))]
    private string _searchQuery = string.Empty;

    public bool IsSearchEmpty => string.IsNullOrWhiteSpace(SearchQuery);

    public string QuickActionTitle => $"对「{(SearchQuery.Length > 24 ? SearchQuery[..24] + "…" : SearchQuery)}」";

    [ObservableProperty]
    private LauncherListItem? _selectedItem;

    [ObservableProperty]
    private int _selectedIndex = -1;

    public ObservableCollection<AppTileItem> RecentApps { get; } = [];
    public ObservableCollection<AppTileItem> PinnedTools { get; } = [];
    public ObservableCollection<QuickActionTile> QuickActions { get; } = [];
    public ObservableCollection<LauncherListItem> SearchResults { get; } = [];

    public event Action? RequestHideWindow;
    public event Action<string, string>? RequestOpenSuperPanel;
    public event Action? RequestOpenClipboard;
    public event Action<int>? RequestOpenSettings;

    public MainViewModel() : this(DesktopAppContext.Instance)
    {
    }

    public MainViewModel(DesktopAppContext context)
    {
        _context = context;

        var launcherSettings = _context.SettingsStore.Current.Launcher;
        if (!launcherSettings.DefaultToolsInitialized && (launcherSettings.PinnedAppIds == null || launcherSettings.PinnedAppIds.Count == 0))
        {
            _context.SettingsStore.Update(s => s with
            {
                Launcher = s.Launcher with
                {
                    DefaultToolsInitialized = true,
                    PinnedAppIds =
                    [
                        "builtin:clipboard",
                        "builtin:superpanel",
                        "builtin:filesearch",
                        "builtin:rename",
                        "builtin:powertimer",
                        "builtin:settings"
                    ]
                }
            });
        }

        LoadDefaultApps();
        BuildQuickActions();

        _context.LauncherSearch.UsageChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            LoadDefaultApps();
        });
        _context.SettingsStore.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(HotkeyDisplay));
            LoadDefaultApps();
        });
    }

    public string HotkeyDisplay => _context.SettingsStore.Current.Launcher.Hotkey;

    public bool IsAppPinned(string id)
    {
        var pinned = _context.SettingsStore.Current.Launcher.PinnedAppIds;
        return pinned != null && pinned.Contains(id, StringComparer.OrdinalIgnoreCase);
    }

    public void TogglePin(string id)
    {
        var current = _context.SettingsStore.Current.Launcher.PinnedAppIds ?? [];
        List<string> updated;
        if (current.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            updated = current.Where(x => !string.Equals(x, id, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else
        {
            updated = [.. current, id];
        }

        _context.SettingsStore.Update(s => s with
        {
            Launcher = s.Launcher with
            {
                PinnedAppIds = updated,
                DefaultToolsInitialized = true
            }
        });

        LoadDefaultApps();
    }

    private AppTileItem? CreateBuiltinTile(string id)
    {
        return id.ToLowerInvariant() switch
        {
            "builtin:clipboard" => new AppTileItem(
                id: "builtin:clipboard",
                name: "剪贴板",
                subtitle: "Alt+V 历史记录",
                badgeColor: "#059669",
                glyph: "C",
                iconBitmap: null,
                iconPathData: LucideIcons.Clipboard,
                isPinned: true,
                canOpenLocation: false,
                canRunAsAdmin: false,
                onLaunch: () =>
                {
                    RequestHideWindow?.Invoke();
                    RequestOpenClipboard?.Invoke();
                },
                onTogglePin: () => TogglePin("builtin:clipboard")),

            "builtin:superpanel" => new AppTileItem(
                id: "builtin:superpanel",
                name: "超级面板",
                subtitle: "Alt+S 划词翻译与AI",
                badgeColor: "#7C3AED",
                glyph: "W",
                iconBitmap: null,
                iconPathData: LucideIcons.Wand,
                isPinned: true,
                canOpenLocation: false,
                canRunAsAdmin: false,
                onLaunch: () =>
                {
                    RequestHideWindow?.Invoke();
                    RequestOpenSuperPanel?.Invoke(string.Empty, "translate");
                },
                onTogglePin: () => TogglePin("builtin:superpanel")),

            "builtin:filesearch" => new AppTileItem(
                id: "builtin:filesearch",
                name: "文件搜索",
                subtitle: "plocate 极速检索",
                badgeColor: "#2563EB",
                glyph: "F",
                iconBitmap: null,
                iconPathData: LucideIcons.Folder,
                isPinned: true,
                canOpenLocation: false,
                canRunAsAdmin: false,
                onLaunch: () =>
                {
                    SearchQuery = "f ";
                },
                onTogglePin: () => TogglePin("builtin:filesearch")),

            "builtin:rename" => new AppTileItem(
                id: "builtin:rename",
                name: "批量重命名",
                subtitle: "规则替换与编号",
                badgeColor: "#D97706",
                glyph: "R",
                iconBitmap: null,
                iconPathData: LucideIcons.Pencil,
                isPinned: true,
                canOpenLocation: false,
                canRunAsAdmin: false,
                onLaunch: () =>
                {
                    RequestHideWindow?.Invoke();
                    RequestOpenSettings?.Invoke(7);
                },
                onTogglePin: () => TogglePin("builtin:rename")),

            "builtin:powertimer" => new AppTileItem(
                id: "builtin:powertimer",
                name: "定时关机",
                subtitle: "到点关机/重启/睡眠",
                badgeColor: "#DC2626",
                glyph: "T",
                iconBitmap: null,
                iconPathData: LucideIcons.Timer,
                isPinned: true,
                canOpenLocation: false,
                canRunAsAdmin: false,
                onLaunch: () =>
                {
                    RequestHideWindow?.Invoke();
                    RequestOpenSettings?.Invoke(8);
                },
                onTogglePin: () => TogglePin("builtin:powertimer")),

            "builtin:settings" => new AppTileItem(
                id: "builtin:settings",
                name: "系统设置",
                subtitle: "Alt+, 偏好配置",
                badgeColor: "#4F46E5",
                glyph: "S",
                iconBitmap: null,
                iconPathData: LucideIcons.Gear,
                isPinned: true,
                canOpenLocation: false,
                canRunAsAdmin: false,
                onLaunch: () =>
                {
                    RequestHideWindow?.Invoke();
                    RequestOpenSettings?.Invoke(0);
                },
                onTogglePin: () => TogglePin("builtin:settings")),

            _ => null
        };
    }

    private AppTileItem CreateAppTile(AppEntry app, bool isPinned)
    {
        var icon = LinuxIconHelper.TryLoadIcon(app.Icon);
        var color = LinuxIconHelper.GetGradientColor(app.DisplayName);
        var glyph = LinuxIconHelper.GetGlyphChar(app.DisplayName);

        return new AppTileItem(
            id: app.Id,
            name: app.DisplayName,
            subtitle: app.Exec,
            badgeColor: color,
            glyph: glyph,
            iconBitmap: icon,
            iconPathData: null,
            isPinned: isPinned,
            canOpenLocation: true,
            canRunAsAdmin: true,
            onLaunch: () =>
            {
                RequestHideWindow?.Invoke();
                _ = _context.AppScanner.LaunchAppAsync(app);
                _context.LauncherSearch.RecordUsage(app.Id);
            },
            onTogglePin: () => TogglePin(app.Id),
            onOpenLocation: () =>
            {
                RequestHideWindow?.Invoke();
                if (!string.IsNullOrEmpty(app.DesktopFilePath))
                {
                    _ = _context.ActionHost.RevealAsync(app.DesktopFilePath);
                }
            },
            onRunAsAdmin: () =>
            {
                RequestHideWindow?.Invoke();
                if (!string.IsNullOrEmpty(app.Exec))
                {
                    _ = _context.ActionHost.OpenAsync(app.Exec, elevated: true);
                }
            });
    }

    private void InitPinnedTools()
    {
        PinnedTools.Clear();

        var pinnedIds = _context.SettingsStore.Current.Launcher.PinnedAppIds;
        if (pinnedIds == null || pinnedIds.Count == 0)
        {
            return;
        }

        var cached = _context.LauncherSearch.CachedApps;

        foreach (var id in pinnedIds)
        {
            if (id.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
            {
                var tile = CreateBuiltinTile(id);
                if (tile != null)
                {
                    PinnedTools.Add(tile);
                }
            }
            else
            {
                var app = cached.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
                if (app != null)
                {
                    PinnedTools.Add(CreateAppTile(app, isPinned: true));
                }
            }
        }
    }

    public void LoadDefaultApps()
    {
        InitPinnedTools();
        RecentApps.Clear();
        int maxCount = Math.Clamp(_context.SettingsStore.Current.Launcher.MaxRecentApps, 1, 28);
        var apps = _context.LauncherSearch.GetRecentApps(maxCount);
        var pinnedIds = _context.SettingsStore.Current.Launcher.PinnedAppIds ?? [];

        foreach (var app in apps)
        {
            bool isPinned = pinnedIds.Contains(app.Id, StringComparer.OrdinalIgnoreCase);
            RecentApps.Add(CreateAppTile(app, isPinned));
        }
    }

    private void BuildQuickActions()
    {
        QuickActions.Clear();

        QuickActions.Add(new QuickActionTile("翻译", LucideIcons.Translate, () =>
        {
            RequestHideWindow?.Invoke();
            RequestOpenSuperPanel?.Invoke(SearchQuery, "translate");
        }));

        QuickActions.Add(new QuickActionTile("问 AI", LucideIcons.Ai, () =>
        {
            RequestHideWindow?.Invoke();
            RequestOpenSuperPanel?.Invoke(SearchQuery, "chat");
        }));

        QuickActions.Add(new QuickActionTile("搜文件", LucideIcons.Folder, () =>
        {
            if (!SearchQuery.StartsWith("f "))
            {
                SearchQuery = "f " + SearchQuery.Trim();
            }
        }));

        QuickActions.Add(new QuickActionTile("总结", LucideIcons.Doc, () =>
        {
            RequestHideWindow?.Invoke();
            RequestOpenSuperPanel?.Invoke(SearchQuery, "summary");
        }));

        QuickActions.Add(new QuickActionTile("必应", LucideIcons.Search, () =>
        {
            RequestHideWindow?.Invoke();
            LaunchWebUrl($"https://cn.bing.com/search?q={Uri.EscapeDataString(SearchQuery)}");
        }));

        QuickActions.Add(new QuickActionTile("网页", LucideIcons.Grid, () =>
        {
            RequestHideWindow?.Invoke();
            LaunchWebUrl($"https://www.baidu.com/s?wd={Uri.EscapeDataString(SearchQuery)}");
        }));
    }

    partial void OnSearchQueryChanged(string value)
    {
        PerformSearch(value);
    }

    private void PerformSearch(string query)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        if (string.IsNullOrWhiteSpace(query))
        {
            SearchResults.Clear();
            SelectedIndex = -1;
            SelectedItem = null;
            return;
        }

        var trimmed = query.Trim();
        bool isFileExplicit = trimmed.StartsWith("f ", StringComparison.OrdinalIgnoreCase);
        var searchTarget = isFileExplicit ? trimmed[2..].Trim() : trimmed;

        if (isFileExplicit && string.IsNullOrWhiteSpace(searchTarget))
        {
            SearchResults.Clear();
            SelectedIndex = -1;
            SelectedItem = null;
            return;
        }

        var currentVersion = Interlocked.Increment(ref _searchVersion);
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var ct = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var finalResult = await _context.LauncherSearch.SearchAsync(searchTarget, partial =>
                {
                    if (ct.IsCancellationRequested || _searchVersion != currentVersion) return;

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ct.IsCancellationRequested || _searchVersion != currentVersion) return;
                        ApplySearchResults(partial, isFileExplicit);
                    });
                }, isFileExplicit: isFileExplicit, cancellationToken: ct).ConfigureAwait(false);

                if (!ct.IsCancellationRequested && _searchVersion == currentVersion)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ct.IsCancellationRequested || _searchVersion != currentVersion) return;
                        ApplySearchResults(finalResult, isFileExplicit);
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // 新输入触发取消，静默忽略
            }
            catch
            {
                // 忽略非致命异常
            }
        }, ct);
    }

    private void ApplySearchResults(LauncherResult result, bool isFileOnly)
    {
        var prevSelectedId = SelectedItem?.CoreItem.Id;

        SearchResults.Clear();

        var sourceItems = isFileOnly
            ? result.Items.Where(i => i.Kind is LauncherItemKind.File or LauncherItemKind.Folder).ToList()
            : result.Items;

        if (sourceItems.Count == 0)
        {
            SelectedIndex = -1;
            SelectedItem = null;
            return;
        }

        string? lastGroup = null;
        int index = 0;
        int restoredIndex = -1;

        foreach (var item in sourceItems)
        {
            bool isFirst = !string.Equals(item.Group, lastGroup, StringComparison.OrdinalIgnoreCase);
            if (isFirst)
            {
                lastGroup = item.Group;
            }

            string hotkey = index < 9 ? $"Alt {index + 1}" : string.Empty;
            var (tag, tagColor) = GetItemTagInfo(item);
            var groupDisplayName = GetGroupDisplayName(item.Group);

            var listItem = new LauncherListItem(
                item,
                tag,
                tagColor,
                hotkey,
                groupDisplayName,
                isFirst,
                _context.LauncherSearch,
                _context.ActionHost,
                () => RequestHideWindow?.Invoke(),
                onTogglePin: TogglePin,
                isPinned: IsAppPinned);

            SearchResults.Add(listItem);

            if (prevSelectedId != null && item.Id == prevSelectedId)
            {
                restoredIndex = index;
            }

            index++;
        }

        if (restoredIndex >= 0 && restoredIndex < SearchResults.Count)
        {
            SelectedIndex = restoredIndex;
            SelectedItem = SearchResults[restoredIndex];
        }
        else if (SearchResults.Count > 0)
        {
            SelectedIndex = 0;
            SelectedItem = SearchResults[0];
        }
    }

    public static (string Tag, string Color) GetItemTagInfo(LauncherItem item)
    {
        if (item.Group == LauncherGroups.Timer)
        {
            return ("定时", "#DC2626");
        }

        return item.Kind switch
        {
            LauncherItemKind.Calculation => ("算式", "#10B981"),
            LauncherItemKind.App => ("应用", "#3B82F6"),
            LauncherItemKind.Folder => ("目录", "#F59E0B"),
            LauncherItemKind.File => ("文件", "#8B5CF6"),
            LauncherItemKind.Command => ("终端", "#06B6D4"),
            LauncherItemKind.Web => ("搜索", "#EC4899"),
            LauncherItemKind.Ai => ("AI", "#7C3AED"),
            LauncherItemKind.Action => ("指令", "#EF4444"),
            _ => ("其它", "#6B7280")
        };
    }

    public static string GetGroupDisplayName(string group) => group switch
    {
        LauncherGroups.Calculation => Strings.Get("Launcher_Group_Calculation"),
        LauncherGroups.Timer => Strings.Get("Launcher_Group_Timer"),
        LauncherGroups.Applications => Strings.Get("Launcher_Group_Applications"),
        LauncherGroups.Recent => Strings.Get("Launcher_Group_Recent"),
        LauncherGroups.Pinned => Strings.Get("Launcher_Group_Pinned"),
        LauncherGroups.Commands => Strings.Get("Launcher_Group_Commands"),
        LauncherGroups.Terminal => Strings.Get("Launcher_Group_Terminal"),
        LauncherGroups.Files => Strings.Get("Launcher_Group_Files"),
        LauncherGroups.Web => Strings.Get("Launcher_Group_Web"),
        LauncherGroups.Ai => Strings.Get("Launcher_Group_Ai"),
        LauncherGroups.Settings => Strings.Get("Launcher_Group_Settings"),
        LauncherGroups.Favorites => Strings.Get("Launcher_Group_Favorites"),
        LauncherGroups.FolderJump => Strings.Get("Launcher_Group_FolderJump"),
        _ => Strings.Get("Launcher_Group_Recent")
    };

    [RelayCommand]
    public void OpenSettings(object? param = null)
    {
        RequestHideWindow?.Invoke();
        int tabIndex = 0;
        if (param is int idx) tabIndex = idx;
        RequestOpenSettings?.Invoke(tabIndex);
    }

    [RelayCommand]
    public void TogglePinCurrent()
    {
        if (SelectedItem != null && SelectedItem.Kind == LauncherItemKind.App)
        {
            var app = SelectedItem.Payload as AppEntry
                      ?? _context.LauncherSearch.CachedApps.FirstOrDefault(a => a.Id == SelectedItem.CoreItem.Id);
            if (app != null)
            {
                var currentPinned = _context.SettingsStore.Current.Launcher.PinnedAppIds.ToList();
                if (currentPinned.Any(id => string.Equals(id, app.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    currentPinned.RemoveAll(id => string.Equals(id, app.Id, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    currentPinned.Add(app.Id);
                }

                _context.SettingsStore.Update(s => s with
                {
                    Launcher = s.Launcher with { PinnedAppIds = currentPinned }
                });

                InitPinnedTools();
            }
        }
    }

    [RelayCommand]
    public async Task ExecuteDefaultAsync()
    {
        if (SelectedItem != null)
        {
            await SelectedItem.ExecuteCommand.ExecuteAsync(null);
            return;
        }

        if (CalculatorService.TryEvaluate(SearchQuery, out var calcVal, out _))
        {
            await _context.ClipboardService.SetTextAsync(calcVal);
            RequestHideWindow?.Invoke();
        }
    }

    [RelayCommand]
    public async Task OpenContainingFolderAsync()
    {
        if (SelectedItem != null)
        {
            await SelectedItem.OpenLocationCommand.ExecuteAsync(null);
            return;
        }

        // 兜底若无选中项且有搜索关键词
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var target = SearchQuery.Trim();
            if (target.StartsWith("f ", StringComparison.OrdinalIgnoreCase)) target = target[2..].Trim();
            if (Directory.Exists(target))
            {
                LaunchFileWithDefaultApp(target);
                RequestHideWindow?.Invoke();
            }
        }
    }

    public void ExecuteIndexedAction(int index)
    {
        if (index >= 0 && index < SearchResults.Count)
        {
            SearchResults[index].ExecuteCommand.Execute(null);
        }
    }

    private static void LaunchFileWithDefaultApp(string targetPath)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    ArgumentList = { targetPath },
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
        }
        catch { }
    }

    private static void LaunchWebUrl(string url)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    ArgumentList = { url },
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
        }
        catch { }
    }
}
