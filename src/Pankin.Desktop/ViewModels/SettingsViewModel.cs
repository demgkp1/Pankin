using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pankin.Core.Ai;
using Pankin.Core.Localization;
using Pankin.Core.Settings;
using Pankin.Desktop.Services;
using Pankin.Desktop.ViewModels.Modules;
using Pankin.Desktop.ViewModels.Updates;

namespace Pankin.Desktop.ViewModels;

public sealed partial class NavTabItem : ObservableObject
{
    public int Index { get; }
    public string Name { get; }
    public string IconPathData { get; }
    public StreamGeometry IconGeometry => LucideIcons.GetGeometry(IconPathData);

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isVisible = true;

    public IRelayCommand SelectCommand { get; }

    public NavTabItem(int index, string name, string iconPathData, bool isSelected, Action<int> onSelect)
    {
        Index = index;
        Name = name;
        IconPathData = iconPathData;
        _isSelected = isSelected;
        SelectCommand = new RelayCommand(() => onSelect(index));
    }
}

public sealed partial class ModuleCardItem : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    public string IconPathData { get; }
    public StreamGeometry IconGeometry => LucideIcons.GetGeometry(IconPathData);
    public string Description { get; }
    public bool HasToolButton { get; }

    [ObservableProperty]
    private bool _isEnabled;

    private readonly Action<bool> _onToggle;
    public IRelayCommand? OpenToolCommand { get; }

    public ModuleCardItem(
        string id,
        string name,
        string iconPathData,
        string description,
        bool isEnabled,
        bool hasToolButton,
        Action<bool> onToggle,
        Action? onOpenTool = null)
    {
        Id = id;
        Name = name;
        IconPathData = iconPathData;
        Description = description;
        _isEnabled = isEnabled;
        HasToolButton = hasToolButton;
        _onToggle = onToggle;

        if (onOpenTool != null)
        {
            OpenToolCommand = new RelayCommand(onOpenTool);
        }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        _onToggle?.Invoke(value);
    }
}

public sealed class RenameFilePreviewItem : ObservableObject
{
    public string OriginalPath { get; }
    public string OriginalName { get; }
    public string NewName { get; }

    public RenameFilePreviewItem(string originalPath, string originalName, string newName)
    {
        OriginalPath = originalPath;
        OriginalName = originalName;
        NewName = newName;
    }
}

public sealed partial class GestureMappingItem : ObservableObject
{
    public string GestureCode { get; }
    public string GestureName { get; }

    [ObservableProperty]
    private string _selectedAction;

    public ObservableCollection<string> AvailableActions { get; } =
    [
        "后退 (Alt+Left)",
        "前进 (Alt+Right)",
        "回到顶部 (Home)",
        "到底部 (End)",
        "关闭标签页 (Ctrl+W)",
        "呼出启动器",
        "呼出剪贴板",
        "呼出超级面板",
        "打开设置",
        "锁定屏幕",
        "无动作"
    ];

    private readonly Action<string> _onActionChanged;

    public GestureMappingItem(string code, string name, string action, Action<string> onActionChanged)
    {
        GestureCode = code;
        GestureName = name;
        _selectedAction = action;
        _onActionChanged = onActionChanged;
    }

    partial void OnSelectedActionChanged(string value)
    {
        _onActionChanged?.Invoke(value);
    }
}

public partial class SettingsViewModel : ViewModelBase
{
    private readonly DesktopAppContext _context;

    // 11 个选项卡显隐控制 (严格对齐原版 Pane-Win)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTabGeneral))]
    [NotifyPropertyChangedFor(nameof(IsTabModules))]
    [NotifyPropertyChangedFor(nameof(IsTabLauncher))]
    [NotifyPropertyChangedFor(nameof(IsTabClipboard))]
    [NotifyPropertyChangedFor(nameof(IsTabGestures))]
    [NotifyPropertyChangedFor(nameof(IsTabSuperPanel))]
    [NotifyPropertyChangedFor(nameof(IsTabFolderJump))]
    [NotifyPropertyChangedFor(nameof(IsTabBatchRename))]
    [NotifyPropertyChangedFor(nameof(IsTabPowerTimer))]
    [NotifyPropertyChangedFor(nameof(IsTabAi))]
    [NotifyPropertyChangedFor(nameof(IsTabAbout))]
    private int _selectedTabIndex;

    public bool IsTabGeneral => SelectedTabIndex == 0;
    public bool IsTabModules => SelectedTabIndex == 1;
    public bool IsTabLauncher => SelectedTabIndex == 2;
    public bool IsTabClipboard => SelectedTabIndex == 3;
    public bool IsTabGestures => SelectedTabIndex == 4;
    public bool IsTabSuperPanel => SelectedTabIndex == 5;
    public bool IsTabFolderJump => SelectedTabIndex == 6;
    public bool IsTabBatchRename => SelectedTabIndex == 7;
    public bool IsTabPowerTimer => SelectedTabIndex == 8;
    public bool IsTabAi => SelectedTabIndex == 9;
    public bool IsTabAbout => SelectedTabIndex == 10;

    // --- Tab 0: 通用 ---
    [ObservableProperty]
    private string _selectedThemeColor = "#5874C9";

    [ObservableProperty]
    private double _panelRadius = 20.0;

    [ObservableProperty]
    private bool _startAtLogin;

    [ObservableProperty]
    private bool _showTrayIcon = true;

    [ObservableProperty]
    private string _selectedLanguage = "system";

    public ObservableCollection<string> AvailableLanguages { get; } =
    [
        "system",
        "zh-CN",
        "en-US"
    ];

    [ObservableProperty]
    private ThemeMode _selectedTheme = ThemeMode.Dark;

    public ObservableCollection<ThemeMode> AvailableThemes { get; } =
    [
        ThemeMode.System,
        ThemeMode.Light,
        ThemeMode.Dark
    ];

    // --- Tab 1: 能力网格 ---
    public ObservableCollection<ModuleCardItem> Modules { get; } = [];

    // --- Tab 2: 启动器 ---
    [ObservableProperty]
    private string _launcherHotkey = "Ctrl+Space";

    [ObservableProperty]
    private bool _doubleTapCtrl = true;

    [ObservableProperty]
    private bool _enableFileSearch = true;

    [ObservableProperty]
    private bool _enableCalculator = true;

    [ObservableProperty]
    private bool _rememberCommands = true;

    [ObservableProperty]
    private bool _webSuggestions = true;

    [ObservableProperty]
    private int _maxAppRows = 2;

    [ObservableProperty]
    private int _maxFileRows = 5;

    [ObservableProperty]
    private string _selectedSearchEngine = "bing";

    public ObservableCollection<string> SearchEngines { get; } =
    [
        "bing",
        "baidu",
        "google",
        "duckduckgo"
    ];

    // --- Tab 3: 剪贴板 ---
    [ObservableProperty]
    private string _clipboardHotkey = "Alt+V";

    [ObservableProperty]
    private int _clipboardMaxItems = 500;

    [ObservableProperty]
    private bool _clipboardFilterEmpty = true;

    [ObservableProperty]
    private bool _clipboardRecordText = true;

    [ObservableProperty]
    private bool _clipboardRecordImages = true;

    // --- Tab 4: 鼠标手势 ---
    [ObservableProperty]
    private string _selectedGestureButton = "Right";

    public ObservableCollection<string> GestureButtons { get; } =
    [
        "Right",
        "Middle",
        "Side"
    ];

    [ObservableProperty]
    private bool _showGestureTrail = true;

    public ObservableCollection<GestureMappingItem> GestureMappings { get; } = [];

    // --- Tab 5: 超级面板 ---
    [ObservableProperty]
    private bool _autoTranslateSelection;

    [ObservableProperty]
    private string _superPanelTrigger = "Side";

    [ObservableProperty]
    private int _superPanelHoldDuration = 260;

    [ObservableProperty]
    private int _selectionMinLength = 2;

    [ObservableProperty]
    private int _selectionMaxLength = 2000;

    [ObservableProperty]
    private bool _pauseInFullscreen = true;

    [ObservableProperty]
    private string _blacklistedAppsInput = string.Empty;

    public ObservableCollection<string> BlacklistedApps { get; } = [];

    // --- Tab 6: 文件夹跳转 (FolderJump) ---
    public ObservableCollection<string> FavoriteFolders { get; } = [];

    [ObservableProperty]
    private string _newFavoriteFolderPath = string.Empty;

    // --- Tab 7: 批量重命名 (BatchRename) ---
    [ObservableProperty]
    private string _renameFolderPath = string.Empty;

    [ObservableProperty]
    private string _renameFindText = string.Empty;

    [ObservableProperty]
    private string _renameReplaceText = string.Empty;

    [ObservableProperty]
    private string _renamePrefix = string.Empty;

    [ObservableProperty]
    private string _renameSuffix = string.Empty;

    [ObservableProperty]
    private int _renameStartNumber = 1;

    [ObservableProperty]
    private bool _renameEnableNumbering;

    [ObservableProperty]
    private string _renameStatusMessage = string.Empty;

    public ObservableCollection<RenameFilePreviewItem> RenamePreviews { get; } = [];

    // --- Tab 8: 定时关机 (PowerTimer - 严格对齐原版功能) ---
    [ObservableProperty]
    private int _powerTimerMinutes = 30;

    [ObservableProperty]
    private string _selectedPowerAction = "关机";

    public ObservableCollection<string> PowerActions { get; } =
    [
        "关机",
        "重启",
        "睡眠",
        "锁屏"
    ];

    [ObservableProperty]
    private bool _isPowerTimerArmed;

    [ObservableProperty]
    private string _powerTimerRemainingText = "--:--";

    [ObservableProperty]
    private string _powerTimerTargetAtText = string.Empty;

    private DispatcherTimer? _powerTimerTicker;
    private DateTimeOffset? _powerTimerTargetTime;
    private bool _hasRemindedPowerTimer;

    // --- Tab 9: AI 服务 ---
    [ObservableProperty]
    private string _aiBaseUrl = "https://api.deepseek.com";

    [ObservableProperty]
    private string _aiApiKey = string.Empty;

    [ObservableProperty]
    private string _aiModel = "deepseek-chat";

    [ObservableProperty]
    private string _aiApiFormat = "OpenAi";

    public ObservableCollection<string> AiApiFormats { get; } =
    [
        "OpenAi",
        "Anthropic"
    ];

    public ObservableCollection<AiPreset> AiPresetsList { get; } = [];

    [ObservableProperty]
    private AiPreset? _selectedPreset;

    public ObservableCollection<NavTabItem> NavTabs { get; } = [];

    public ModulesSettingsViewModel ModulesSettings { get; }
    public UpdateViewModel UpdateSettings { get; }

    public SettingsViewModel() : this(DesktopAppContext.Instance)
    {
    }

    public SettingsViewModel(DesktopAppContext context)
    {
        _context = context;

        ModulesSettings = new ModulesSettingsViewModel(_context.Modules);
        UpdateSettings = new UpdateViewModel(_context.UpdateService);

        InitNavTabs();
        InitModulesGrid();
        InitGestureMappings();
        InitAiPresets();
        LoadFromSettingsStore();

        _context.Modules.Changed += (_, _) => Dispatcher.UIThread.Post(UpdateNavTabsVisibility);
    }

    private void InitNavTabs()
    {
        NavTabs.Clear();
        (string Key, string IconPath)[] tabs =
        [
            ("Settings_NavGeneral", LucideIcons.Gear),
            ("Settings_NavModules", LucideIcons.Grid),
            ("Settings_NavLauncher", LucideIcons.Search),
            ("Settings_NavClipboard", LucideIcons.Clipboard),
            ("Settings_NavGestures", LucideIcons.Mouse),
            ("Settings_NavSuperPanel", LucideIcons.Wand),
            ("Settings_NavFolderJump", LucideIcons.Folder),
            ("Settings_NavBatchRename", LucideIcons.Pencil),
            ("Settings_NavPowerTimer", LucideIcons.Timer),
            ("Settings_NavAi", LucideIcons.Ai),
            ("Settings_NavAbout", LucideIcons.Doc)
        ];

        for (int i = 0; i < tabs.Length; i++)
        {
            var tab = tabs[i];
            var tabName = Strings.Get(tab.Key);
            NavTabs.Add(new NavTabItem(i, tabName, tab.IconPath, i == SelectedTabIndex, idx =>
            {
                SwitchTab(idx);
            }));
        }

        UpdateNavTabsVisibility();
    }

    public void UpdateNavTabsVisibility()
    {
        var m = _context.SettingsStore.Current.Modules;
        foreach (var tab in NavTabs)
        {
            tab.IsVisible = tab.Index switch
            {
                0 => true,
                1 => true,
                2 => m.Launcher,
                3 => m.Clipboard,
                4 => m.Gestures,
                5 => m.SuperPanel,
                6 => m.FolderJump,
                7 => m.BatchRename,
                8 => m.PowerTimer,
                9 => m.Ai,
                10 => true,
                _ => true
            };
        }

        var curTab = NavTabs.FirstOrDefault(t => t.Index == SelectedTabIndex);
        if (curTab != null && !curTab.IsVisible)
        {
            SwitchTab(1);
        }
    }

    private void InitModulesGrid()
    {
        Modules.Clear();
        var m = _context.SettingsStore.Current.Modules;

        Modules.Add(new ModuleCardItem("launcher", "启动器", LucideIcons.Search, "热键呼出，搜应用、文件，算算式，搜网页", m.Launcher, false,
            enabled =>
            {
                SaveSetting(s => s with { Modules = s.Modules with { Launcher = enabled } });
                UpdateNavTabsVisibility();
            }));

        Modules.Add(new ModuleCardItem("clipboard", "剪贴板", LucideIcons.Clipboard, "记下复制过的文字、代码和链接，随时找回来再粘贴", m.Clipboard, false,
            enabled =>
            {
                SaveSetting(s => s with { Modules = s.Modules with { Clipboard = enabled } });
                UpdateNavTabsVisibility();
            }));

        Modules.Add(new ModuleCardItem("gestures", "鼠标手势", LucideIcons.Mouse, "按住右键/侧键画方向，执行快捷键、窗口操作等动作", m.Gestures, false,
            enabled =>
            {
                SaveSetting(s => s with { Modules = s.Modules with { Gestures = enabled } });
                UpdateNavTabsVisibility();
            }));

        Modules.Add(new ModuleCardItem("super", "超级面板", LucideIcons.Wand, "选中文字后长按鼠标侧键或自动弹出，就地翻译、总结、润色", m.SuperPanel, false,
            enabled =>
            {
                SaveSetting(s => s with { Modules = s.Modules with { SuperPanel = enabled } });
                UpdateNavTabsVisibility();
            }));

        Modules.Add(new ModuleCardItem("folder", "文件夹跳转", LucideIcons.Folder, "常用目录收藏，在启动器一键直达", m.FolderJump, false,
            enabled =>
            {
                SaveSetting(s => s with { Modules = s.Modules with { FolderJump = enabled } });
                UpdateNavTabsVisibility();
            }));

        Modules.Add(new ModuleCardItem("rename", "批量重命名", LucideIcons.Pencil, "一次给多个文件改名：替换文字、加编号、改扩展名，先预览再执行", m.BatchRename, true,
            enabled =>
            {
                SaveSetting(s => s with { Modules = s.Modules with { BatchRename = enabled } });
                UpdateNavTabsVisibility();
            }, () => SwitchToTab("批量重命名")));

        Modules.Add(new ModuleCardItem("power-timer", "定时关机", LucideIcons.Timer, "到点关机、重启或睡眠；到点前 1 分钟提醒，可以取消或推迟", m.PowerTimer, true,
            enabled =>
            {
                SaveSetting(s => s with { Modules = s.Modules with { PowerTimer = enabled } });
                UpdateNavTabsVisibility();
            }, () => SwitchToTab("定时关机")));

        Modules.Add(new ModuleCardItem("ai", "AI", LucideIcons.Ai, "翻译、提问和对文字的快捷动作；在 AI 设置里接入服务", m.Ai, false,
            enabled =>
            {
                SaveSetting(s => s with { Modules = s.Modules with { Ai = enabled } });
                UpdateNavTabsVisibility();
            }));
    }

    private void InitGestureMappings()
    {
        GestureMappings.Clear();
        var bindings = _context.SettingsStore.Current.Gestures.Bindings;

        (string Code, string Name)[] templates =
        [
            ("L", "左滑 (L)"),
            ("R", "右滑 (R)"),
            ("U", "上滑 (U)"),
            ("D", "下滑 (D)"),
            ("DR", "右下 (DR)"),
            ("DL", "左下 (DL)"),
            ("UL", "左上 (UL)"),
            ("UR", "右上 (UR)"),
            ("Hold", "长按 (Hold)")
        ];

        foreach (var t in templates)
        {
            var existing = bindings.FirstOrDefault(b => string.Equals(b.Direction, t.Code, StringComparison.OrdinalIgnoreCase));
            string action = existing?.Action ?? (t.Code == "Hold" ? "呼出超级面板" : "无动作");

            GestureMappings.Add(new GestureMappingItem(t.Code, t.Name, action, newAction =>
            {
                SaveGestureBindings();
            }));
        }
    }

    private void SaveGestureBindings()
    {
        var list = GestureMappings
            .Where(m => m.SelectedAction != "无动作")
            .Select(m => new GestureBindingConfig(m.GestureCode, m.SelectedAction))
            .ToList();

        SaveSetting(s => s with { Gestures = s.Gestures with { Bindings = list } });
    }

    private void InitAiPresets()
    {
        AiPresetsList.Clear();
        foreach (var p in AiPresets.All)
        {
            AiPresetsList.Add(p);
        }
    }

    public void SwitchTab(int index)
    {
        if (index < 0 || index >= NavTabs.Count) return;
        SelectedTabIndex = index;
        foreach (var t in NavTabs)
        {
            t.IsSelected = t.Index == index;
        }
    }

    public void SwitchToTab(string tabName)
    {
        var target = NavTabs.FirstOrDefault(t => t.Name == tabName);
        if (target != null)
        {
            SwitchTab(target.Index);
        }
    }

    private bool _isLoadingSettings;

    private void LoadFromSettingsStore()
    {
        _isLoadingSettings = true;
        try
        {
            var settings = _context.SettingsStore.Current;

            // 通用
            SelectedLanguage = settings.General.UiLanguage ?? "system";
            SelectedTheme = settings.General.Theme;
            PanelRadius = settings.General.PanelRadius;
            StartAtLogin = settings.General.StartAtLogin;
            ShowTrayIcon = settings.General.ShowTrayIcon;
            SelectedThemeColor = settings.General.AccentColor;

            // 启动器
            LauncherHotkey = settings.Launcher.Hotkey;
            DoubleTapCtrl = settings.Launcher.DoubleTapCtrl;
            SelectedSearchEngine = settings.Launcher.DefaultSearchEngine;
            RememberCommands = settings.Launcher.RememberCommands;
            WebSuggestions = settings.Launcher.WebSuggestions;
            MaxAppRows = settings.Launcher.MaxAppRows;
            MaxFileRows = settings.Launcher.MaxFileRows;

            // 剪贴板
            ClipboardHotkey = settings.Clipboard.Hotkey;
            ClipboardMaxItems = settings.Clipboard.HistoryLimit;

            // 鼠标手势
            SelectedGestureButton = settings.Gestures.TriggerButton;
            ShowGestureTrail = settings.Gestures.ShowTrail;

            // 超级面板
            AutoTranslateSelection = settings.Ai.AutoTranslateSelection;
            SuperPanelTrigger = settings.SuperPanel.TriggerButton;
            SuperPanelHoldDuration = settings.SuperPanel.HoldDurationMs;
            SelectionMinLength = settings.SuperPanel.SelectionMinLength;
            SelectionMaxLength = settings.SuperPanel.SelectionMaxLength;
            PauseInFullscreen = settings.SuperPanel.PauseInFullscreen;
            BlacklistedApps.Clear();
            foreach (var b in settings.SuperPanel.BlacklistedApps)
            {
                BlacklistedApps.Add(b);
            }

            // 文件夹跳转
            FavoriteFolders.Clear();
            if (settings.Launcher.FavoriteFolders.Count > 0)
            {
                foreach (var f in settings.Launcher.FavoriteFolders)
                {
                    FavoriteFolders.Add(f);
                }
            }
            else
            {
                var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string[] defaults = [
                    Path.Combine(userHome, "Downloads"),
                    Path.Combine(userHome, "Documents"),
                    Path.Combine(userHome, "Pictures"),
                    Path.Combine(userHome, "Desktop")
                ];
                foreach (var d in defaults)
                {
                    if (Directory.Exists(d)) FavoriteFolders.Add(d);
                }
            }

            // AI 服务
            AiBaseUrl = settings.Ai.BaseUrl;
            AiApiKey = settings.Ai.ApiKey ?? string.Empty;
            AiModel = settings.Ai.Model;
            AiApiFormat = settings.Ai.ApiFormat;

            SelectedPreset = AiPresetsList.FirstOrDefault(p => p.Model == settings.Ai.Model) ?? AiPresetsList.FirstOrDefault();
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    partial void OnPanelRadiusChanged(double value)
    {
        SaveSetting(s => s with { General = s.General with { PanelRadius = value } });
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        SaveSetting(s => s with { General = s.General with { UiLanguage = value } });
        var resolved = UiLanguageResolver.Resolve(value, UiLanguageResolver.SystemLanguage());
        Strings.Initialize(resolved);
        InitNavTabs();
    }

    partial void OnSelectedThemeChanged(ThemeMode value)
    {
        SaveSetting(s => s with { General = s.General with { Theme = value } });
        if (Avalonia.Application.Current != null)
        {
            Avalonia.Application.Current.RequestedThemeVariant = value switch
            {
                ThemeMode.Light => Avalonia.Styling.ThemeVariant.Light,
                ThemeMode.Dark => Avalonia.Styling.ThemeVariant.Dark,
                _ => Avalonia.Styling.ThemeVariant.Default
            };
        }
    }

    partial void OnStartAtLoginChanged(bool value)
    {
        _context.AutostartService.SetAutostart(value);
        SaveSetting(s => s with { General = s.General with { StartAtLogin = value } });
    }

    partial void OnShowTrayIconChanged(bool value)
    {
        SaveSetting(s => s with { General = s.General with { ShowTrayIcon = value } });
    }

    partial void OnRememberCommandsChanged(bool value)
    {
        SaveSetting(s => s with { Launcher = s.Launcher with { RememberCommands = value } });
    }

    partial void OnWebSuggestionsChanged(bool value)
    {
        SaveSetting(s => s with { Launcher = s.Launcher with { WebSuggestions = value } });
    }

    partial void OnMaxAppRowsChanged(int value)
    {
        SaveSetting(s => s with { Launcher = s.Launcher with { MaxAppRows = value } });
    }

    partial void OnMaxFileRowsChanged(int value)
    {
        SaveSetting(s => s with { Launcher = s.Launcher with { MaxFileRows = value } });
    }

    partial void OnSelectionMinLengthChanged(int value)
    {
        SaveSetting(s => s with { SuperPanel = s.SuperPanel with { SelectionMinLength = value } });
    }

    partial void OnSelectionMaxLengthChanged(int value)
    {
        SaveSetting(s => s with { SuperPanel = s.SuperPanel with { SelectionMaxLength = value } });
    }

    partial void OnPauseInFullscreenChanged(bool value)
    {
        SaveSetting(s => s with { SuperPanel = s.SuperPanel with { PauseInFullscreen = value } });
    }

    [RelayCommand]
    public void AddBlacklistedApp()
    {
        if (!string.IsNullOrWhiteSpace(BlacklistedAppsInput))
        {
            var app = BlacklistedAppsInput.Trim();
            if (!BlacklistedApps.Contains(app))
            {
                BlacklistedApps.Add(app);
                BlacklistedAppsInput = string.Empty;
                SaveSetting(s => s with { SuperPanel = s.SuperPanel with { BlacklistedApps = BlacklistedApps.ToList() } });
            }
        }
    }

    [RelayCommand]
    public void RemoveBlacklistedApp(string app)
    {
        if (BlacklistedApps.Remove(app))
        {
            SaveSetting(s => s with { SuperPanel = s.SuperPanel with { BlacklistedApps = BlacklistedApps.ToList() } });
        }
    }

    partial void OnSelectedGestureButtonChanged(string value)
    {
        SaveSetting(s => s with { Gestures = s.Gestures with { TriggerButton = value } });
    }

    partial void OnAutoTranslateSelectionChanged(bool value)
    {
        SaveSetting(s => s with { Ai = s.Ai with { AutoTranslateSelection = value } });
    }

    partial void OnSelectedPresetChanged(AiPreset? value)
    {
        if (value == null) return;
        AiBaseUrl = value.BaseUrl;
        AiModel = value.Model;
        AiApiFormat = value.Format.ToString();
    }

    [RelayCommand]
    public void SaveAiConfig()
    {
        SaveSetting(s => s with
        {
            Ai = s.Ai with
            {
                BaseUrl = AiBaseUrl,
                ApiKey = string.IsNullOrWhiteSpace(AiApiKey) ? null : AiApiKey.Trim(),
                Model = AiModel,
                ApiFormat = AiApiFormat
            }
        });

        ShowNotification("Pankin 设置", "AI 配置已成功保存！");
    }

    // --- 文件夹跳转操作 ---
    [RelayCommand]
    public void AddFavoriteFolder()
    {
        if (!string.IsNullOrWhiteSpace(NewFavoriteFolderPath) && Directory.Exists(NewFavoriteFolderPath.Trim()))
        {
            var p = NewFavoriteFolderPath.Trim();
            if (!FavoriteFolders.Contains(p))
            {
                FavoriteFolders.Add(p);
                NewFavoriteFolderPath = string.Empty;
                SaveSetting(s => s with { Launcher = s.Launcher with { FavoriteFolders = FavoriteFolders.ToList() } });
            }
        }
    }

    [RelayCommand]
    public void RemoveFavoriteFolder(string path)
    {
        if (FavoriteFolders.Remove(path))
        {
            SaveSetting(s => s with { Launcher = s.Launcher with { FavoriteFolders = FavoriteFolders.ToList() } });
        }
    }

    [RelayCommand]
    public void OpenFavoriteFolder(string path)
    {
        LaunchFileWithDefaultApp(path);
    }

    // --- 批量重命名逻辑 ---
    [RelayCommand]
    public void LoadRenameFiles()
    {
        RenamePreviews.Clear();
        RenameStatusMessage = string.Empty;

        var path = string.IsNullOrWhiteSpace(RenameFolderPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : RenameFolderPath.Trim();

        if (!Directory.Exists(path))
        {
            RenameStatusMessage = "指定的目录不存在";
            return;
        }

        try
        {
            var files = Directory.GetFiles(path);
            int number = RenameStartNumber;

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                var ext = Path.GetExtension(file);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(file);

                string newName = nameWithoutExt;

                if (!string.IsNullOrEmpty(RenameFindText))
                {
                    newName = newName.Replace(RenameFindText, RenameReplaceText);
                }

                if (!string.IsNullOrEmpty(RenamePrefix)) newName = RenamePrefix + newName;
                if (!string.IsNullOrEmpty(RenameSuffix)) newName = newName + RenameSuffix;

                if (RenameEnableNumbering)
                {
                    newName = $"{newName}_{number:D2}";
                    number++;
                }

                newName += ext;
                RenamePreviews.Add(new RenameFilePreviewItem(file, fileName, newName));
            }

            RenameStatusMessage = $"已扫描到 {RenamePreviews.Count} 个文件";
        }
        catch (Exception ex)
        {
            RenameStatusMessage = $"扫描失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ExecuteRename()
    {
        if (RenamePreviews.Count == 0) return;

        int successCount = 0;
        try
        {
            foreach (var item in RenamePreviews)
            {
                if (item.OriginalName == item.NewName) continue;

                var dir = Path.GetDirectoryName(item.OriginalPath)!;
                var targetPath = Path.Combine(dir, item.NewName);

                if (!File.Exists(targetPath))
                {
                    File.Move(item.OriginalPath, targetPath);
                    successCount++;
                }
            }

            RenameStatusMessage = $"成功重命名 {successCount} 个文件！";
            LoadRenameFiles();
        }
        catch (Exception ex)
        {
            RenameStatusMessage = $"重命名执行中断: {ex.Message}";
        }
    }

    // --- 定时关机 (PowerTimer) 严格实现 ---
    [RelayCommand]
    public void SetQuickDelay(string minutesStr)
    {
        if (int.TryParse(minutesStr, out int m))
        {
            PowerTimerMinutes = m;
        }
    }

    [RelayCommand]
    public void ArmPowerTimer()
    {
        if (PowerTimerMinutes < 1) PowerTimerMinutes = 1;
        _powerTimerTargetTime = DateTimeOffset.Now.AddMinutes(PowerTimerMinutes);
        _hasRemindedPowerTimer = false;
        IsPowerTimerArmed = true;
        PowerTimerTargetAtText = $"将于 {_powerTimerTargetTime.Value:HH:mm:ss} 执行 {SelectedPowerAction}";

        _powerTimerTicker?.Stop();
        _powerTimerTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _powerTimerTicker.Tick += OnPowerTimerTick;
        _powerTimerTicker.Start();
        UpdatePowerTimerDisplay();
    }

    [RelayCommand]
    public void CancelPowerTimer()
    {
        _powerTimerTicker?.Stop();
        _powerTimerTicker = null;
        _powerTimerTargetTime = null;
        IsPowerTimerArmed = false;
        PowerTimerRemainingText = "--:--";
        PowerTimerTargetAtText = "定时已取消";
    }

    private void OnPowerTimerTick(object? sender, EventArgs e)
    {
        if (_powerTimerTargetTime == null) return;
        var left = _powerTimerTargetTime.Value - DateTimeOffset.Now;

        if (left <= TimeSpan.Zero)
        {
            _powerTimerTicker?.Stop();
            _powerTimerTicker = null;
            IsPowerTimerArmed = false;
            PowerTimerRemainingText = "00:00";
            ExecutePowerAction(SelectedPowerAction);
            return;
        }

        UpdatePowerTimerDisplay();

        if (left <= TimeSpan.FromSeconds(60) && !_hasRemindedPowerTimer)
        {
            _hasRemindedPowerTimer = true;
            ShowNotification("Pankin 定时关机提醒", $"系统将在 1 分钟后执行「{SelectedPowerAction}」，如需取消请打开 Pankin。");
        }
    }

    private void UpdatePowerTimerDisplay()
    {
        if (_powerTimerTargetTime == null) return;
        var left = _powerTimerTargetTime.Value - DateTimeOffset.Now;
        if (left > TimeSpan.Zero)
        {
            int totalMins = (int)left.TotalMinutes;
            PowerTimerRemainingText = $"{totalMins:D2}:{left.Seconds:D2}";
        }
    }

    private static void ExecutePowerAction(string action)
    {
        try
        {
            if (action.Contains("关机"))
            {
                Process.Start(new ProcessStartInfo("systemctl", "poweroff") { UseShellExecute = false, CreateNoWindow = true });
            }
            else if (action.Contains("重启"))
            {
                Process.Start(new ProcessStartInfo("systemctl", "reboot") { UseShellExecute = false, CreateNoWindow = true });
            }
            else if (action.Contains("睡眠"))
            {
                Process.Start(new ProcessStartInfo("systemctl", "suspend") { UseShellExecute = false, CreateNoWindow = true });
            }
            else if (action.Contains("锁屏"))
            {
                Process.Start(new ProcessStartInfo("loginctl", "lock-session") { UseShellExecute = false, CreateNoWindow = true });
            }
        }
        catch { }
    }

    private void SaveSetting(Func<PankinSettings, PankinSettings> update)
    {
        if (_isLoadingSettings) return;
        _context.SettingsStore.Update(update);
    }

    private static void LaunchFileWithDefaultApp(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open", path) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch { }
    }

    private static void ShowNotification(string title, string body)
    {
        try
        {
            Process.Start(new ProcessStartInfo("notify-send", $"\"{title}\" \"{body}\"") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch { }
    }
}
