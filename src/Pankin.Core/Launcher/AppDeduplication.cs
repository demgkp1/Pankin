using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pankin.Core.Abstractions;

namespace Pankin.Core.Launcher;

/// <summary>
/// 应用程序使用频率与时间记录
/// </summary>
public sealed record AppUsage(int Count, DateTimeOffset LastUsed);

[JsonSerializable(typeof(Dictionary<string, AppUsage>))]
internal partial class LauncherUsageJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Linux 桌面应用去重、稳定标识符生成与频次/近期使用加权算法
/// </summary>
public static class AppDeduplication
{
    /// <summary>
    /// 生成应用的跨会话稳定 SHA256 唯一标识符
    /// </summary>
    public static string GenerateStableId(AppEntry app)
    {
        var rawKey = $"{app.Id}\n{NormalizeExec(app.Exec)}\n{app.Name}".Trim().ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return "app:" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// 标准化 Exec 命令（去除参数占位符如 %U, %f 等）
    /// </summary>
    public static string NormalizeExec(string exec)
    {
        if (string.IsNullOrWhiteSpace(exec)) return string.Empty;
        var parts = exec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return string.Empty;
        var bin = Path.GetFileName(parts[0].Trim('"'));
        return bin.ToLowerInvariant();
    }

    /// <summary>
    /// 对扫描出的所有 Desktop 应用条目进行智能去重合并
    /// </summary>
    public static IReadOnlyList<AppEntry> DeduplicateApps(IEnumerable<AppEntry> apps)
    {
        var list = apps.ToList();
        var result = new List<AppEntry>();

        // 优先按 Desktop Id 分组
        var byId = list.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase);
        var firstPass = new List<AppEntry>();

        foreach (var group in byId)
        {
            // 用户目录 (~/.local/share) 优先于系统目录 (/usr/share)
            var chosen = group
                .OrderBy(a => a.DesktopFilePath.Contains(".local/share") ? 0 : 1)
                .ThenByDescending(a => !string.IsNullOrEmpty(a.LocalizedName))
                .First();
            firstPass.Add(chosen);
        }

        // 二次去重：同名且指向相同底层可执行文件的重复桌面项（如 flatpak 与系统 deb 重复）
        var byExecAndName = firstPass.GroupBy(a => (NormalizeExec(a.Exec), a.DisplayName.ToLowerInvariant()));
        foreach (var group in byExecAndName)
        {
            var chosen = group
                .OrderBy(a => a.DesktopFilePath.Contains(".local/share") ? 0 : 1)
                .ThenBy(a => a.Id.Length)
                .First();
            result.Add(chosen);
        }

        return result;
    }

    /// <summary>
    /// 合并旧应用标识的使用频次记录到新标识
    /// </summary>
    public static bool MergeUsage(Dictionary<string, AppUsage> usage, IReadOnlyDictionary<string, string> aliases)
    {
        bool changed = false;
        foreach (var (oldId, newId) in aliases)
        {
            if (string.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase)) continue;

            if (usage.Remove(oldId, out var oldUsage))
            {
                changed = true;
                if (usage.TryGetValue(newId, out var existing))
                {
                    var mergedCount = Math.Min(int.MaxValue - 1, existing.Count + oldUsage.Count);
                    var mergedLastUsed = existing.LastUsed > oldUsage.LastUsed ? existing.LastUsed : oldUsage.LastUsed;
                    usage[newId] = new AppUsage(mergedCount, mergedLastUsed);
                }
                else
                {
                    usage[newId] = oldUsage;
                }
            }
        }
        return changed;
    }

    /// <summary>
    /// 计算应用匹配的综合排序得分（匹配度 + 频次加成 + 访问近期度加成）
    /// </summary>
    public static int CalculateScore(int baseMatchScore, AppUsage? usage, DateTimeOffset now)
    {
        int score = baseMatchScore * 10;
        if (usage == null) return score;

        // 频次增益 (Log2 加权，最多 +25 分)
        int freqBoost = Math.Min(25, (int)(Math.Log2(usage.Count + 1) * 6));

        // 近期使用时间增益 (24小时内 +20, 7天内 +12, 30天内 +5)
        var elapsed = now - usage.LastUsed;
        int recencyBoost = 0;
        if (elapsed <= TimeSpan.FromHours(24))
        {
            recencyBoost = 20;
        }
        else if (elapsed <= TimeSpan.FromDays(7))
        {
            recencyBoost = 12;
        }
        else if (elapsed <= TimeSpan.FromDays(30))
        {
            recencyBoost = 5;
        }

        return score + freqBoost + recencyBoost;
    }
}
