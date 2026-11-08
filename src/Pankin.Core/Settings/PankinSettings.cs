namespace Pankin.Core.Settings;

public enum ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2
}

public sealed record GeneralSettings
{
    public ThemeMode Theme { get; init; } = ThemeMode.Dark;
    public string AccentColor { get; init; } = "#8B8DFF";
    public double PanelRadius { get; init; } = 16.0;
    public double PanelOpacity { get; init; } = 0.94;
    public bool HideOnDeactivate { get; init; } = true;
    public bool RememberWindowPosition { get; init; } = true;
    public int? LauncherPositionX { get; init; }
    public int? LauncherPositionY { get; init; }
    public bool StartAtLogin { get; init; } = false;
    public bool ShowTrayIcon { get; init; } = true;
    public string? UiLanguage { get; init; } = "system";
    public bool SetupCompleted { get; init; } = false;
}

public sealed record ModulesSettings
{
    public bool Launcher { get; init; } = true;
    public bool Clipboard { get; init; } = true;
    public bool Gestures { get; init; } = true;
    public bool SuperPanel { get; init; } = true;
    public bool Ai { get; init; } = true;
    public bool FolderJump { get; init; } = true;
    public bool BatchRename { get; init; } = true;
    public bool PowerTimer { get; init; } = true;
}

public sealed record LauncherSettings
{
    public string Hotkey { get; init; } = "Ctrl+Space";
    public bool DoubleTapCtrl { get; init; } = true;
    public int MaxAppRows { get; init; } = 2;
    public int MaxFileRows { get; init; } = 5;
    public string DefaultSearchEngine { get; init; } = "bing";
    public bool RememberCommands { get; init; } = true;
    public bool WebSuggestions { get; init; } = true;
    public int MaxRecentApps { get; init; } = 14;
    public bool DefaultToolsInitialized { get; init; } = true;
    public IReadOnlyList<string> PinnedAppIds { get; init; } =
    [
        "builtin:clipboard",
        "builtin:superpanel",
        "builtin:filesearch",
        "builtin:rename",
        "builtin:powertimer",
        "builtin:settings"
    ];
    public IReadOnlyList<string> FavoriteFolders { get; init; } = [];
}

public sealed record ClipboardConfig
{
    public string Hotkey { get; init; } = "Alt+V";
    public bool Enabled { get; init; } = true;
    public int HistoryLimit { get; init; } = 500;
    public bool RecordText { get; init; } = true;
    public bool RecordImages { get; init; } = true;
    public bool RecordFiles { get; init; } = true;
    public bool AutoDedupe { get; init; } = true;
}

public sealed record AiConfigSettings
{
    public bool Enabled { get; init; } = true;
    public string BaseUrl { get; init; } = "https://api.deepseek.com";
    public string? ApiKey { get; init; }
    public string Model { get; init; } = "deepseek-chat";
    public string? TranslationModel { get; init; }
    public string TargetLanguage { get; init; } = "auto";
    public string ApiFormat { get; init; } = "OpenAi";
    public bool AutoTranslateSelection { get; init; } = false;
}

public sealed record GestureBindingConfig(string Direction, string Action);

public sealed record GestureSettings
{
    public bool Enabled { get; init; } = true;
    public string TriggerButton { get; init; } = "Right"; // "Right", "Middle", "Side"
    public bool ShowTrail { get; init; } = true;
    public string TrailColor { get; init; } = "#5874C9";
    public int MinimumDistance { get; init; } = 32;
    public bool AllowDiagonal { get; init; } = true;
    public IReadOnlyList<GestureBindingConfig> Bindings { get; init; } =
    [
        new("L", "后退 (Alt+Left)"),
        new("R", "前进 (Alt+Right)"),
        new("U", "回到顶部 (Home)"),
        new("D", "到底部 (End)"),
        new("DR", "关闭标签页 (Ctrl+W)"),
        new("UL", "呼出启动器"),
        new("UR", "呼出剪贴板"),
        new("Hold", "呼出超级面板")
    ];
}

public sealed record SuperPanelSettings
{
    public string Hotkey { get; init; } = "Alt+S";
    public bool Enabled { get; init; } = true;
    public string TriggerButton { get; init; } = "Side";
    public int HoldDurationMs { get; init; } = 260;
    public bool FollowCursor { get; init; } = true;
    public bool TriggerOnSelection { get; init; } = true;
    public bool TriggerOnDoubleClick { get; init; } = true;
    public bool TriggerOnMouseUp { get; init; } = true;
    public int SelectionMinLength { get; init; } = 2;
    public int SelectionMaxLength { get; init; } = 2000;
    public bool RestoreClipboardAfterBorrow { get; init; } = true;
    public bool PauseInFullscreen { get; init; } = true;
    public bool ClipboardFallback { get; init; } = true;
    public IReadOnlyList<string> BlacklistedApps { get; init; } = [];
    public IReadOnlyList<string> EnabledActions { get; init; } =
    [
        "copy-translation",
        "translate",
        "chat",
        "summary",
        "explain",
        "copy",
        "search",
        "open-link"
    ];
}

public sealed record UpdateSettings
{
    public bool AutoUpdate { get; init; } = true;
    public bool CheckOnStartup { get; init; } = true;
    public DateTimeOffset? LastChecked { get; init; }
    public string? IgnoredVersion { get; init; }
}

public sealed record PankinSettings
{
    public int Version { get; init; } = 1;
    public GeneralSettings General { get; init; } = new();
    public ModulesSettings Modules { get; init; } = new();
    public LauncherSettings Launcher { get; init; } = new();
    public ClipboardConfig Clipboard { get; init; } = new();
    public AiConfigSettings Ai { get; init; } = new();
    public GestureSettings Gestures { get; init; } = new();
    public SuperPanelSettings SuperPanel { get; init; } = new();
    public UpdateSettings Update { get; init; } = new();
}
