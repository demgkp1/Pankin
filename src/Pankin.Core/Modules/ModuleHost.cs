using System.Collections.Concurrent;
using Pankin.Core.Settings;

namespace Pankin.Core.Modules;

/// <summary>
/// 模块宿主实现：管理全部能力的依赖拓扑、生命周期调度、状态持久化与并发控制
/// </summary>
public sealed class ModuleHost : IModuleCatalog
{
    private readonly Dictionary<string, ICapabilityModule> _modules;
    private readonly ConcurrentDictionary<string, ModuleState> _states = new(StringComparer.Ordinal);
    private readonly Func<string, bool> _isEnabled;
    private readonly Action<string, bool> _saveChoice;
    private readonly Func<bool> _restores;
    private readonly Func<Func<Task>, Task> _dispatch;
    private readonly SemaphoreSlim _serial = new(1, 1);

    public IReadOnlyList<ModuleInfo> Modules { get; }

    public event EventHandler<string>? Changed;

    /// <summary>
    /// 标准构造器：绑定到 SettingsStore
    /// </summary>
    public ModuleHost(
        IEnumerable<ICapabilityModule> entries,
        SettingsStore store,
        Func<bool>? restores = null,
        Func<Func<Task>, Task>? dispatch = null)
        : this(
            entries,
            id => ModuleSettingsMapping.GetModuleEnabled(store.Current, id, true),
            (id, enabled) => store.Update(s => ModuleSettingsMapping.SetModuleEnabled(s, id, enabled)),
            restores,
            dispatch)
    {
    }

    /// <summary>
    /// 解耦构造器：支持自定义存储代理或单元测试
    /// </summary>
    public ModuleHost(
        IEnumerable<ICapabilityModule> entries,
        Func<string, bool> isEnabled,
        Action<string, bool> saveChoice,
        Func<bool>? restores = null,
        Func<Func<Task>, Task>? dispatch = null)
    {
        _modules = entries.ToDictionary(m => m.Info.Id, StringComparer.Ordinal);
        Modules = _modules.Values.Select(m => m.Info).ToArray();
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        _saveChoice = saveChoice ?? throw new ArgumentNullException(nameof(saveChoice));
        _restores = restores ?? (() => false);
        _dispatch = dispatch ?? (work => work());

        foreach (var m in Modules)
        {
            _states[m.Id] = new ModuleState(m.Id, false, ModuleStatus.Disabled);
        }

        // 校验硬依赖是否存在有向环
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Validate(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id))
            {
                throw new ArgumentException($"能力硬依赖成环: {id}");
            }

            if (_modules.TryGetValue(id, out var module))
            {
                foreach (var dep in module.Info.Dependencies.Where(d => d.Kind == DependencyKind.Required))
                {
                    Validate(dep.ModuleId);
                }
            }

            visiting.Remove(id);
            visited.Add(id);
        }

        foreach (var m in Modules)
        {
            Validate(m.Id);
        }
    }

    public ModuleState StateOf(string id)
    {
        if (_states.TryGetValue(id, out var state))
        {
            return state;
        }
        return new ModuleState(id, false, ModuleStatus.Disabled, "未登记模块");
    }

    public bool IsActive(string id)
    {
        return _states.TryGetValue(id, out var s) && s.Status == ModuleStatus.Running;
    }

    private bool Enabled(string id)
    {
        if (!_modules.TryGetValue(id, out var m)) return false;
        return _isEnabled(id);
    }

    public ModuleChange Preview(string id, bool enable)
    {
        if (!_modules.ContainsKey(id))
        {
            throw new ArgumentException("未知能力标识", nameof(id));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { id };
        var related = new List<string>();

        void Visit(string item)
        {
            IEnumerable<string> next;
            if (enable)
            {
                // 启用时：顺着 Required 依赖找前置模块
                next = _modules.TryGetValue(item, out var mod)
                    ? mod.Info.Dependencies.Where(d => d.Kind == DependencyKind.Required).Select(d => d.ModuleId)
                    : Enumerable.Empty<string>();
            }
            else
            {
                // 停用时：找所有硬依赖于 item 的后置模块
                next = Modules
                    .Where(m => m.Dependencies.Any(d => d.Kind == DependencyKind.Required && d.ModuleId == item))
                    .Select(m => m.Id);
            }

            foreach (var n in next)
            {
                if (seen.Add(n))
                {
                    Visit(n);
                    if (Enabled(n) != enable)
                    {
                        related.Add(n);
                    }
                }
            }
        }

        Visit(id);

        bool restoresSystem = !enable &&
            (id == ModuleIds.Clipboard || related.Contains(ModuleIds.Clipboard)) &&
            _restores();

        return new ModuleChange(
            Id: id,
            Enable: enable,
            AlsoEnable: enable ? related : Array.Empty<string>(),
            AlsoDisable: enable ? Array.Empty<string>() : related,
            RestartRequired: false,
            RestoresSystemSetting: restoresSystem);
    }

    public Task InitializeAsync(CancellationToken token = default) => _dispatch(async () =>
    {
        var attempted = new HashSet<string>(StringComparer.Ordinal);

        async Task Start(string id)
        {
            if (!attempted.Add(id)) return;

            if (_modules.TryGetValue(id, out var mod))
            {
                foreach (var dep in mod.Info.Dependencies.Where(d => d.Kind == DependencyKind.Required))
                {
                    await Start(dep.ModuleId);
                }
            }

            if (!Enabled(id))
            {
                _saveChoice(id, true);
            }

            await Change(id, true, token, persist: false);
        }

        foreach (var m in Modules)
        {
            if (Enabled(m.Id))
            {
                await Start(m.Id);
            }
            else
            {
                await Change(m.Id, false, token, persist: false);
            }
        }
    });

    public Task ApplyAsync(ModuleChange change, CancellationToken cancellationToken = default) => _dispatch(async () =>
    {
        await _serial.WaitAsync(cancellationToken);
        try
        {
            // 重新计算最新规划，避免过时或非法的连带列表
            var plan = Preview(change.Id, change.Enable);
            var cascade = plan.Enable ? plan.AlsoEnable : plan.AlsoDisable;

            foreach (var id in cascade)
            {
                if (!await Change(id, plan.Enable, cancellationToken))
                {
                    return;
                }
            }

            await Change(plan.Id, plan.Enable, cancellationToken);
        }
        finally
        {
            _serial.Release();
        }
    });

    private async Task<bool> Change(string id, bool enable, CancellationToken token, bool persist = true)
    {
        token.ThrowIfCancellationRequested();
        if (!_modules.TryGetValue(id, out var module)) return false;

        try
        {
            if (enable && persist)
            {
                _saveChoice(id, true);
            }

            if (enable && module.Info.Dependencies.Any(d => d.Kind == DependencyKind.Required && !IsActive(d.ModuleId)))
            {
                throw new InvalidOperationException($"模块 '{id}' 的前置硬依赖尚未运行");
            }

            if (enable && !IsActive(id))
            {
                await module.StartAsync(token);
            }

            if (!enable && (!persist || StateOf(id).Status != ModuleStatus.Disabled))
            {
                await module.StopAsync(token);
            }

            if (!enable && persist)
            {
                _saveChoice(id, false);
            }

            _states[id] = new ModuleState(id, enable, enable ? ModuleStatus.Running : ModuleStatus.Disabled);
        }
        catch (Exception ex)
        {
            if (enable)
            {
                try { await module.StopAsync(CancellationToken.None); } catch { }
            }

            var problemMsg = ex.Message.Split('\n')[0];
            _states[id] = new ModuleState(id, Enabled(id), ModuleStatus.Failed, problemMsg);
            Changed?.Invoke(this, id);
            return false;
        }

        Changed?.Invoke(this, id);
        return true;
    }
}
