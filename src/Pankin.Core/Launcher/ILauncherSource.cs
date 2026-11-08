using Pankin.Core.Abstractions;
using Pankin.Core.Settings;

namespace Pankin.Core.Launcher;

/// <summary>
/// 每次搜索执行时的上下文快照
/// </summary>
public sealed record SourceContext(
    string Query,
    IReadOnlyList<AppEntry> Apps,
    LauncherSettings Settings,
    IReadOnlyDictionary<string, AppUsage> Usage,
    PankinSettings AllSettings,
    bool IsFileExplicit = false)
{
    public PankinSettings FullSettings => AllSettings;
}

/// <summary>
/// 启动器搜索来源接口
/// </summary>
public interface ILauncherSource
{
    string Group { get; }
    int Order { get; }
    int Limit(SourceContext context);
    TimeSpan Timeout { get; }
    Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext context, CancellationToken token);
    bool Owns(LauncherItem item);
    Task ExecuteAsync(LauncherItem item, SourceContext context, CancellationToken token);
}

/// <summary>
/// 启动器搜索来源基类
/// </summary>
public abstract class LauncherSource(string group, int order, string prefix) : ILauncherSource
{
    public string Group => group;
    public int Order => order;
    public virtual TimeSpan Timeout => TimeSpan.FromSeconds(2);
    public abstract int Limit(SourceContext context);
    public virtual bool Owns(LauncherItem item) => item.Id.StartsWith(prefix, StringComparison.Ordinal);
    public abstract Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext context, CancellationToken token);
    public abstract Task ExecuteAsync(LauncherItem item, SourceContext context, CancellationToken token);

    protected static Task<IReadOnlyList<LauncherItem>> Rows(IEnumerable<LauncherItem> rows) =>
        Task.FromResult<IReadOnlyList<LauncherItem>>(rows.ToArray());
}
