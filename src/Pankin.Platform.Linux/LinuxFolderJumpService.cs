using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pankin.Core.FolderJump;

namespace Pankin.Platform.Linux;

/// <summary>
/// Linux 平台下的文件夹跳转与活跃文件管理器检测实现。
/// 
/// 【Linux 平台特性与局限性说明 (Honest Limitations)】：
/// 1. Wayland 环境限制：在纯 Wayland 会话下，合成器实施进程间安全隔离，未提供全局遍历外部窗口与注入事件的标准接口。
/// 2. GTK / Qt 文件对话框限制：Linux 下的 GtkFileChooserDialog 及 xdg-desktop-portal 对话框不像 Windows 32770 对话框具备公开的 Win32/COM 控件树，无法通过无感句柄直接重定向文件对话框路径。
/// 3. 本实现方案采用最大努力（Best-Effort）与可靠降级（Reliable Fallback）设计：
///    - 在 X11 / XWayland 环境下，通过 LinuxWindowInfoResolver (xprop 120ms 短超时) 嗅探 Nautilus/Dolphin/Thunar 等文件管理器的前台窗口与标题；
///    - 无论何种桌面环境，均通过 Launcher 端快速跳转面板 + xdg-open 系统原生打开 + 路径复制到剪贴板，提供稳定可靠的跨桌面使用体验。
/// </summary>
public sealed class LinuxFolderJumpService : IFolderJumpService
{
    private readonly FolderJumpStore _store;

    public LinuxFolderJumpService(FolderJumpStore? store = null)
    {
        _store = store ?? new FolderJumpStore();
    }

    public async Task<IReadOnlyList<FolderJumpEntry>> GetEntriesAsync(int limit = 15, CancellationToken ct = default)
    {
        var activeFolder = await GetActiveFileManagerFolderAsync(ct).ConfigureAwait(false);
        var storedEntries = await _store.GetEntriesAsync(limit, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(activeFolder) || !Directory.Exists(activeFolder))
        {
            return storedEntries;
        }

        var result = new List<FolderJumpEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 若当前前台存在活跃文件管理器窗口，优先置顶展示
        var activeName = Path.GetFileName(activeFolder.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(activeName)) activeName = activeFolder;
        result.Add(new FolderJumpEntry(activeFolder, $"当前窗口: {activeName}", FolderJumpSource.ActiveFileManager, 999, DateTimeOffset.UtcNow, false));
        seen.Add(activeFolder);

        // 2. 补齐历史/高频/固定条目
        foreach (var entry in storedEntries)
        {
            if (seen.Add(entry.Path))
            {
                result.Add(entry);
                if (result.Count >= limit) break;
            }
        }

        return result;
    }

    public Task<IReadOnlyList<FolderJumpEntry>> SearchAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        return _store.SearchAsync(query, limit, ct);
    }

    public Task RecordAccessAsync(string path, CancellationToken ct = default)
    {
        return _store.RecordAccessAsync(path, ct);
    }

    public Task TogglePinAsync(string path, CancellationToken ct = default)
    {
        return _store.TogglePinAsync(path, ct);
    }

    public async Task<string?> GetActiveFileManagerFolderAsync(CancellationToken ct = default)
    {
        try
        {
            var winInfo = await LinuxWindowInfoResolver.GetActiveWindowInfoAsync(ct).ConfigureAwait(false);
            if (winInfo == null || string.IsNullOrWhiteSpace(winInfo.Title))
            {
                return null;
            }

            var appName = winInfo.AppName?.ToLowerInvariant() ?? string.Empty;
            var title = winInfo.Title.Trim();

            // 若标题直接是绝对路径
            if (title.StartsWith("/") && Directory.Exists(title))
            {
                return title;
            }

            // 判断是否属于已知 Linux 文件管理器
            bool isFileManager = appName.Contains("nautilus") ||
                                 appName.Contains("dolphin") ||
                                 appName.Contains("thunar") ||
                                 appName.Contains("nemo") ||
                                 appName.Contains("pcmanfm") ||
                                 appName.Contains("caja") ||
                                 appName.Contains("io.elementary.files");

            if (!isFileManager)
            {
                return null;
            }

            // 解析常见标题规则，如 "Documents — Files"、"下载 — 文件"、"Downloads — Dolphin"
            string folderName = title;
            int sepIndex = title.LastIndexOf(" — ", StringComparison.Ordinal);
            if (sepIndex < 0) sepIndex = title.LastIndexOf(" - ", StringComparison.Ordinal);
            if (sepIndex > 0)
            {
                folderName = title[..sepIndex].Trim();
            }

            // 在用户常用目录中解析该目录名
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new[]
            {
                Path.Combine(home, folderName),
                Path.Combine(home, "Downloads", folderName),
                Path.Combine(home, "Documents", folderName),
                Path.Combine(home, "Desktop", folderName)
            };

            foreach (var candidate in candidates)
            {
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            // 检查历史记录中是否有该名称的文件夹
            var history = await _store.GetEntriesAsync(50, ct).ConfigureAwait(false);
            var matched = history.FirstOrDefault(h => string.Equals(h.Name, folderName, StringComparison.OrdinalIgnoreCase) ||
                                                      string.Equals(Path.GetFileName(h.Path.TrimEnd(Path.DirectorySeparatorChar)), folderName, StringComparison.OrdinalIgnoreCase));
            if (matched != null && Directory.Exists(matched.Path))
            {
                return matched.Path;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> JumpToFolderAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    ArgumentList = { path },
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (proc.Start())
            {
                await _store.RecordAccessAsync(path, ct).ConfigureAwait(false);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public Task ClearHistoryAsync(CancellationToken ct = default)
    {
        return _store.ClearHistoryAsync(ct);
    }
}
