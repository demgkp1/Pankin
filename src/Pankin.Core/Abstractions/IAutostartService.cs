namespace Pankin.Core.Abstractions;

/// <summary>
/// 系统开机/登录时自启动控制抽象接口
/// </summary>
public interface IAutostartService
{
    /// <summary>
    /// 当前是否已配置为开机/登录自启动
    /// </summary>
    bool IsAutostartEnabled { get; }

    /// <summary>
    /// 启用或关闭开机/登录自启动
    /// </summary>
    void SetAutostart(bool enabled);
}
