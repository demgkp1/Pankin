namespace Pankin.Core.Abstractions;

/// <summary>
/// 跨平台启动器外部动作与操作系统交互抽象宿主
/// </summary>
public interface ILauncherActionHost
{
    /// <summary>
    /// 打开文件、文件夹或 URL
    /// </summary>
    Task<bool> OpenAsync(string pathOrUrl, bool elevated = false, CancellationToken ct = default);

    /// <summary>
    /// 在文件管理器中定位并选中文件，或打开所在目录
    /// </summary>
    Task<bool> RevealAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// 在系统终端模拟器中执行命令
    /// </summary>
    Task<bool> RunInTerminalAsync(string command, bool elevated = false, string? workingDir = null, CancellationToken ct = default);

    /// <summary>
    /// 复制文本到系统剪贴板
    /// </summary>
    Task<bool> CopyTextAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// 执行系统级电源或会话命令 (如 "lock", "sleep", "restart", "shutdown", "logout", "recycle")
    /// </summary>
    Task<bool> ExecuteSystemCommandAsync(string command, CancellationToken ct = default);

    /// <summary>
    /// 显示文件或目录属性
    /// </summary>
    Task<bool> ShowPropertiesAsync(string path, CancellationToken ct = default);
}
