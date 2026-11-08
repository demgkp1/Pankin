namespace Pankin.Core.Updates;

/// <summary>
/// 软件更新状态机枚举
/// </summary>
public enum UpdateState
{
    /// <summary>初始未检查状态</summary>
    Idle,
    /// <summary>正在发起网络检查</summary>
    Checking,
    /// <summary>当前已是最新版本</summary>
    UpToDate,
    /// <summary>已发现新版本但未触发后续动作</summary>
    Available,
    /// <summary>正在下载资源</summary>
    Downloading,
    /// <summary>已就绪，等待用户触发更新/查看发布详情</summary>
    Ready,
    /// <summary>检查、下载或拉起失败</summary>
    Failed
}

/// <summary>
/// 更新失败发生阶段（用于指导 UI 呈现“重试检查”或“重试下载”）
/// </summary>
public enum UpdateStep
{
    Check,
    Download,
    Install
}

/// <summary>
/// 新版本发布信息模型
/// </summary>
/// <param name="Version">新版本号，例如 0.1.100</param>
/// <param name="Notes">去除 Markdown 格式的纯文本版本摘要（最大约 2000 字）</param>
/// <param name="PublishedAt">发布时间</param>
/// <param name="SizeBytes">发布包或归档大小（字节）</param>
/// <param name="PageUrl">GitHub Release 详情网页链接</param>
/// <param name="Details">结构化更新明细（## 新增 / ## 改进 / ## 修复）</param>
public sealed record UpdateRelease(
    string Version,
    string Notes,
    DateTimeOffset PublishedAt,
    long SizeBytes,
    string PageUrl,
    ReleaseNotes? Details = null);

/// <summary>
/// 软件更新服务接口
/// </summary>
public interface IUpdateService
{
    /// <summary>当前正在运行的程序版本</summary>
    string CurrentVersion { get; }

    /// <summary>当前更新状态</summary>
    UpdateState State { get; }

    /// <summary>状态为 Failed 时对应的失败阶段</summary>
    UpdateStep? FailedStep { get; }

    /// <summary>检查到的最新版本信息；最新或未检查时为 null</summary>
    UpdateRelease? Latest { get; }

    /// <summary>下载进度（0.0 ~ 1.0）</summary>
    double DownloadProgress { get; }

    /// <summary>失败原因用户友好描述</summary>
    string? Error { get; }

    /// <summary>最近一次成功或尝试检查的时间</summary>
    DateTimeOffset? LastChecked { get; }

    /// <summary>状态或属性变更事件</summary>
    event EventHandler? Changed;

    /// <summary>
    /// 触发检查新版本（支持取消）
    /// </summary>
    Task CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 触发下载（在 Linux 模式下无 MSI 安装包时自动标记 Ready 并就绪）
    /// </summary>
    Task DownloadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 触发就绪后的更新动作（在 Linux 下弹出通知并在默认浏览器打开 Release 页面）
    /// </summary>
    void InstallAndRestart();
}
