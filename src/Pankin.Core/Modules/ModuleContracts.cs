namespace Pankin.Core.Modules;

/// <summary>
/// 能力的稳定标识，存进 settings.json，不可随意变更。显示名称和说明由界面资源或本地化中心给出。
/// </summary>
public static class ModuleIds
{
    public const string Launcher = "launcher";
    public const string Clipboard = "clipboard";
    public const string Gestures = "gestures";
    public const string SuperPanel = "super-panel";
    public const string Ai = "ai";
    public const string FolderJump = "folder-jump";
    public const string Rename = "rename";
    public const string PowerTimer = "power-timer";
}

/// <summary>
/// 「能力」设置页的分组类别
/// </summary>
public enum ModuleCategory
{
    Search,
    Clipboard,
    Mouse,
    Ai,
    Files,
    System
}

/// <summary>
/// Required = 硬依赖：对方未启用时自身无法启用（启用时由界面确认同时启用）；
/// Optional = 软依赖：对方在则展示联动入口，不在则隐藏，自身正常运作。
/// </summary>
public enum DependencyKind
{
    Required,
    Optional
}

public sealed record ModuleDependency(string ModuleId, DependencyKind Kind);

/// <summary>
/// BuiltIn = Pankin 自带；Community = 社区贡献/外部扩展。
/// </summary>
public enum ModuleSource
{
    BuiltIn,
    Community
}

/// <summary>
/// 模块元数据描述
/// </summary>
/// <param name="Id">稳定唯一标识</param>
/// <param name="Category">分组类别</param>
/// <param name="Glyph">Lucide 图标名</param>
/// <param name="DefaultEnabled">新用户默认开启状态</param>
/// <param name="Dependencies">依赖项列表</param>
/// <param name="Source">来源</param>
/// <param name="Author">作者（社区扩展使用）</param>
public sealed record ModuleInfo(
    string Id,
    ModuleCategory Category,
    string Glyph,
    bool DefaultEnabled,
    IReadOnlyList<ModuleDependency> Dependencies,
    ModuleSource Source = ModuleSource.BuiltIn,
    string? Author = null);

public enum ModuleStatus
{
    /// <summary>启用并在正常运行</summary>
    Running,
    /// <summary>用户停用状态</summary>
    Disabled,
    /// <summary>用户启用但启动失败（例如钩子初始化失败），附带原因给用户看</summary>
    Failed,
    /// <summary>更改了启用状态，需要重启 Pankin 后生效</summary>
    RestartPending
}

/// <summary>
/// 模块运行时状态
/// </summary>
/// <param name="Id">模块标识</param>
/// <param name="Enabled">用户配置的开启选择</param>
/// <param name="Status">运行时状态</param>
/// <param name="Problem">失败或待重启时的提示信息</param>
public sealed record ModuleState(
    string Id,
    bool Enabled,
    ModuleStatus Status,
    string? Problem = null);

/// <summary>
/// 启用或停用一项模块产生的连带变化计算结果。界面先计算 Preview，若有连带依赖弹窗确认，用户同意后再 Apply。
/// </summary>
/// <param name="Id">目标模块标识</param>
/// <param name="Enable">是否启用</param>
/// <param name="AlsoEnable">连带启用的硬依赖项</param>
/// <param name="AlsoDisable">连带停用的项（硬依赖于该模块的其他模块）</param>
/// <param name="RestartRequired">是否需要重启才能生效</param>
/// <param name="RestoresSystemSetting">停用时是否需要恢复某项系统设置（例如系统剪贴板快捷键）</param>
public sealed record ModuleChange(
    string Id,
    bool Enable,
    IReadOnlyList<string> AlsoEnable,
    IReadOnlyList<string> AlsoDisable,
    bool RestartRequired,
    bool RestoresSystemSetting);

/// <summary>
/// 模块能力目录与生命周期管理服务接口
/// </summary>
public interface IModuleCatalog
{
    /// <summary>已登记的全部能力模块列表，按展示顺序排列</summary>
    IReadOnlyList<ModuleInfo> Modules { get; }

    /// <summary>获取指定模块的运行时状态</summary>
    ModuleState StateOf(string id);

    /// <summary>指定模块是否处于正常运行中 (Running)</summary>
    bool IsActive(string id);

    /// <summary>计算启用/停用变更将引发的连带依赖变更（只读纯函数，不改变任何系统状态）</summary>
    ModuleChange Preview(string id, bool enable);

    /// <summary>
    /// 应用变更：更新配置并启动或停止对应模块。
    /// 停止模块时应释放其底层输入钩子、定时器、线程与窗口资源。
    /// </summary>
    Task ApplyAsync(ModuleChange change, CancellationToken cancellationToken = default);

    /// <summary>模块状态发生变更事件（参数为 ModuleId）</summary>
    event EventHandler<string>? Changed;
}
