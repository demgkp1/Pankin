namespace Pankin.Core.Abstractions;

public sealed record AppEntry(
    string Id,
    string Name,
    string? LocalizedName,
    string Exec,
    string? Icon,
    string DesktopFilePath,
    string? WorkingDirectory = null,
    bool Terminal = false)
{
    public string DisplayName => !string.IsNullOrWhiteSpace(LocalizedName) ? LocalizedName! : Name;
}

public sealed record FileSearchResult(
    string FullPath,
    string FileName,
    string DirectoryPath,
    bool IsDirectory = false);

public enum GlobalHotkeyAction
{
    ToggleLauncher,
    ToggleClipboard,
    ToggleSuperPanel,
    OpenSettings
}

public sealed record GlobalHotkeyEventArgs(GlobalHotkeyAction Action, string GestureText);

public sealed record GesturePoint(double X, double Y, long TimestampMs);

public sealed record MouseGestureProgressEventArgs(
    bool IsActive,
    string CurrentDirection,
    IReadOnlyList<GesturePoint> Trajectory,
    double CurrentX,
    double CurrentY);

public sealed record MouseGestureEventArgs(
    string GestureDirection,
    IReadOnlyList<GesturePoint> Trajectory,
    double StartX,
    double StartY,
    double EndX,
    double EndY);

public sealed record SelectionTriggeredEventArgs(
    string SelectedText,
    double MouseX,
    double MouseY,
    string? SourceAppName = null,
    string? WindowTitle = null,
    IReadOnlyList<string>? Files = null);

/// <summary>
/// 跨平台应用扫描与启动接口
/// </summary>
public interface IAppScanner
{
    Task<IReadOnlyList<AppEntry>> ScanAppsAsync(CancellationToken ct = default);
    IReadOnlyList<AppEntry> SearchApps(string query, int maxResults = 20);
    Task<bool> LaunchAppAsync(AppEntry app, CancellationToken ct = default);
}

/// <summary>
/// 本地文件毫秒级检索接口
/// </summary>
public interface IFileSearcher
{
    IAsyncEnumerable<FileSearchResult> SearchFilesAsync(string query, int limit = 50, CancellationToken ct = default);
}

/// <summary>
/// 剪贴板读写、历史监听与主选区（Primary Selection）划词获取接口
/// </summary>
public interface IClipboardService : IDisposable
{
    event EventHandler<string>? ClipboardTextChanged;
    event EventHandler<string>? PrimarySelectionChanged;

    Task<string?> GetTextAsync(CancellationToken ct = default);
    Task SetTextAsync(string text, CancellationToken ct = default);
    Task<string?> GetPrimarySelectionTextAsync(CancellationToken ct = default);
    Task<Selection.ClipboardSnapshot> CreateSnapshotAsync(CancellationToken ct = default);
    Task RestoreSnapshotAsync(Selection.ClipboardSnapshot snapshot, CancellationToken ct = default);

    void StartMonitoring();
    void StopMonitoring();
}

/// <summary>
/// 全局快捷键与鼠标划词监听接口
/// </summary>
public interface IGlobalInputHook : IDisposable
{
    event EventHandler<GlobalHotkeyEventArgs>? HotkeyTriggered;
    event EventHandler<SelectionTriggeredEventArgs>? SelectionTriggered;
    event EventHandler<GesturePoint>? GlobalPointerPressed;

    bool SuperPanelEnabled { get; set; }
    void UpdateHotkeys(string launcherHotkey, string clipboardHotkey, string superPanelHotkey);
    void ConfigureSelectionFilter(Pankin.Core.Settings.SuperPanelSettings settings);

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
}
