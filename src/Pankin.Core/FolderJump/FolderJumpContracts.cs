using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pankin.Core.FolderJump;

public enum FolderJumpSource
{
    ActiveFileManager,
    Frequent,
    Recent,
    Pinned
}

public sealed record FolderJumpEntry(
    string Path,
    string Name,
    FolderJumpSource Source,
    int AccessCount = 0,
    DateTimeOffset? LastAccessed = null,
    bool IsPinned = false);

/// <summary>
/// 跨平台文件夹快速跳转与历史管理抽象服务接口。
/// </summary>
public interface IFolderJumpService
{
    /// <summary>
    /// 获取当前可跳转的文件夹条目（活跃前台文件管理器、固定项、高频与最近访问去重聚合）。
    /// </summary>
    Task<IReadOnlyList<FolderJumpEntry>> GetEntriesAsync(int limit = 15, CancellationToken ct = default);

    /// <summary>
    /// 拼音与模糊搜索历史/常用文件夹。
    /// </summary>
    Task<IReadOnlyList<FolderJumpEntry>> SearchAsync(string query, int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// 记录文件夹访问（自增访问频次、更新访问时间戳）。
    /// </summary>
    Task RecordAccessAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// 固定 / 取消固定文件夹。
    /// </summary>
    Task TogglePinAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// 获取当前前台活跃文件管理器或文件对话框所在的文件夹路径（若无法探测则返回 null）。
    /// </summary>
    Task<string?> GetActiveFileManagerFolderAsync(CancellationToken ct = default);

    /// <summary>
    /// 跳转至目标文件夹（在系统文件管理器中打开或降级定位）。
    /// </summary>
    Task<bool> JumpToFolderAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// 清除所有历史访问记录。
    /// </summary>
    Task ClearHistoryAsync(CancellationToken ct = default);
}
