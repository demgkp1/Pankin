namespace Pankin.Core.Modules.Hosting;

public readonly record struct PanelPoint(double X, double Y);

public readonly record struct PanelSize(double Width, double Height);

/// <summary>
/// 效率面板吸附或展示位置策略
/// </summary>
public enum PanelPlacement
{
    UpperCenter,
    Pointer,
    BottomCenter,
    Center,
    Tray
}

/// <summary>
/// 面板显示上下文（前台活动窗口句柄、锚点物理坐标、是否激活窗口）
/// </summary>
public sealed record PanelShowContext(
    nint PreviousForegroundWindow,
    PanelPoint? AnchorPhysical,
    bool Activate);

/// <summary>
/// 基础桌面面板视图抽象接口（启动器、剪贴板、超级面板共用）
/// </summary>
public interface IPanelView
{
    PanelSize PreferredSize { get; }
    event EventHandler<PanelSize>? SizeRequested;
    event EventHandler? CloseRequested;
    void OnShowing(PanelShowContext context);
    void OnShown();
    void OnHidden();
}

/// <summary>
/// 二级持久窗口抽象接口（批量重命名、翻译面板、AI 会话窗口）
/// </summary>
public interface ISecondaryView : IPanelView
{
    event EventHandler<bool>? TopmostChanged;
    PanelSize MinimumSize { get; }
}
