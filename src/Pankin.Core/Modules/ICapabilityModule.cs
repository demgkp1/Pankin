namespace Pankin.Core.Modules;

/// <summary>
/// 模块能力单元接口：模块持有宿主并受宿主统一生命周期调度
/// </summary>
public interface ICapabilityModule
{
    /// <summary>模块元数据与依赖定义</summary>
    ModuleInfo Info { get; }

    /// <summary>启动模块能力（注册热键、启动后台服务等）</summary>
    Task StartAsync(CancellationToken token);

    /// <summary>停止模块能力（释放钩子、定时器、线程与窗口）</summary>
    Task StopAsync(CancellationToken token);
}

/// <summary>
/// 基于委托的轻量通用能力模块适配器
/// </summary>
public sealed class DelegateCapabilityModule : ICapabilityModule
{
    public ModuleInfo Info { get; }
    private readonly Func<CancellationToken, Task>? _onStart;
    private readonly Func<CancellationToken, Task>? _onStop;

    public DelegateCapabilityModule(
        ModuleInfo info,
        Func<CancellationToken, Task>? onStart = null,
        Func<CancellationToken, Task>? onStop = null)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        _onStart = onStart;
        _onStop = onStop;
    }

    public Task StartAsync(CancellationToken token) => _onStart?.Invoke(token) ?? Task.CompletedTask;
    public Task StopAsync(CancellationToken token) => _onStop?.Invoke(token) ?? Task.CompletedTask;
}
