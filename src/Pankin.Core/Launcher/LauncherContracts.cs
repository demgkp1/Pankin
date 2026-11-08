namespace Pankin.Core.Launcher;

public enum LauncherItemKind
{
    App,
    File,
    Folder,
    Command,
    Calculation,
    Web,
    Ai,
    Action
}

/// <summary>
/// 启动器条目图标信息
/// </summary>
public sealed record LauncherIcon(string Glyph, string? ImagePath = null, string? GradientFrom = null, string? GradientTo = null);

/// <summary>
/// 启动器搜索结果单项
/// </summary>
public sealed record LauncherItem(
    string Id,
    LauncherItemKind Kind,
    string Group,
    string Title,
    string? Subtitle,
    LauncherIcon Icon,
    IReadOnlyList<TextSpan>? Highlights = null,
    string? Hotkey = null,
    int Score = 0,
    object? Tag = null);

/// <summary>
/// 启动器搜索完整结果
/// </summary>
public sealed record LauncherResult(IReadOnlyList<LauncherItem> Items, bool IsHome, int FileTotal = 0);

/// <summary>
/// 启动器分组标识
/// </summary>
public static class LauncherGroups
{
    public const string Applications = "applications";
    public const string Files = "files";
    public const string Commands = "commands";
    public const string Calculation = "calculation";
    public const string Ai = "ai";
    public const string Web = "web";
    public const string Settings = "settings";
    public const string Terminal = "terminal";
    public const string Recent = "recent";
    public const string Pinned = "pinned";
    public const string Completion = "completion";
    public const string Favorites = "favorites";
    public const string FolderJump = "folder-jump";
    public const string Images = "images";
    public const string Other = "other";
    public const string Actions = "actions";
    public const string Timer = "timer";
}

/// <summary>
/// 启动器条目上下文附加操作
/// </summary>
public enum LauncherItemAction
{
    RunAsAdmin,
    OpenLocation,
    CopyPath,
    Properties,
    Uninstall,
    RemoveFromApps
}

public sealed class LauncherActionException(string message) : Exception(message);

/// <summary>
/// 搜索引擎定义
/// </summary>
public sealed record SearchEngine(string Keyword, string Name, string UrlTemplate);

/// <summary>
/// 终端历史记录管理接口
/// </summary>
public interface ITerminalHistory
{
    IReadOnlyList<string> Match(string prefix);
    void Record(string raw);
    void Clear();
}

/// <summary>
/// 网页搜索联想建议接口
/// </summary>
public interface IWebSuggestions
{
    Task<IReadOnlyList<string>> SuggestAsync(SearchEngine engine, string query, CancellationToken cancellationToken);
}

/// <summary>
/// 启动器条目快捷上下文菜单接口
/// </summary>
public interface ILauncherItemActions
{
    Task<IReadOnlyList<LauncherItemAction>> GetActionsAsync(LauncherItem item, CancellationToken cancellationToken = default);
    Task RunAsync(LauncherItem item, LauncherItemAction action, CancellationToken cancellationToken = default);
}

/// <summary>
/// 启动器主搜索聚合服务契约
/// </summary>
public interface ILauncherSearch
{
    Task<LauncherResult> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<LauncherResult> SearchAsync(string query, Action<LauncherResult> partial, CancellationToken cancellationToken = default);
    Task ExecuteAsync(LauncherItem item, CancellationToken cancellationToken = default);
    Task ExecuteElevatedAsync(LauncherItem item, CancellationToken cancellationToken = default);
}

/// <summary>
/// 定时关机/重启/睡眠操作枚举
/// </summary>
public enum PowerAction
{
    Shutdown,
    Restart,
    Sleep
}

/// <summary>
/// 定时关机状态
/// </summary>
public sealed record PowerTimerState(PowerAction Action, DateTimeOffset At, bool Reminding);

/// <summary>
/// 定时执行结局
/// </summary>
public enum PowerTimerOutcome
{
    Executed,
    Missed,
    Failed
}

/// <summary>
/// 定时结果事件载荷
/// </summary>
public sealed record PowerTimerResult(PowerAction Action, PowerTimerOutcome Outcome, string? Detail = null);

/// <summary>
/// 电源定时器服务接口
/// </summary>
public interface IPowerTimer
{
    PowerTimerState? Current { get; }
    void Schedule(PowerAction action, DateTimeOffset at);
    void Cancel();
    void Postpone(TimeSpan by);
    event EventHandler? Changed;
    event EventHandler<PowerTimerResult>? Finished;
}
