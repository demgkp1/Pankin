namespace Pankin.Core.Updates;

/// <summary>
/// 更新说明分类：新增、改进、修复
/// </summary>
public enum ReleaseNoteKind
{
    New,
    Improved,
    Fixed
}

/// <summary>
/// 单条更新说明项
/// </summary>
/// <param name="Kind">类型</param>
/// <param name="Text">对用户友好的说明文本</param>
/// <param name="Highlight">是否为本版本核心亮点（更新提示弹条优先使用）</param>
public sealed record ReleaseNoteItem(ReleaseNoteKind Kind, string Text, bool Highlight = false);

/// <summary>
/// 单个版本的结构化更新说明
/// </summary>
/// <param name="Version">版本号，如 0.1.100</param>
/// <param name="Date">发布日期</param>
/// <param name="Items">更新条目列表</param>
public sealed record ReleaseNotes(string Version, DateOnly? Date, IReadOnlyList<ReleaseNoteItem> Items);

/// <summary>
/// 历史版本更新说明目录接口（支持离线本地缓存或内置资源，无需联网）
/// </summary>
public interface IReleaseNotesCatalog
{
    /// <summary>全部版本更新说明，按版本号从新到旧降序排列</summary>
    IReadOnlyList<ReleaseNotes> All { get; }

    /// <summary>查询指定版本的更新说明，支持数字等价匹配（例如 0.1.36 与 0.1.036 匹配同一版）</summary>
    ReleaseNotes? Find(string version);
}
