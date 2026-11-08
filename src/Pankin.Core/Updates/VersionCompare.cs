using System.Text.RegularExpressions;

namespace Pankin.Core.Updates;

/// <summary>
/// 跨平台版本号比较与校验工具（逐段数字比较，支持语义化版本元数据解析，严禁依赖 Windows 原生 API）
/// </summary>
public static class VersionCompare
{
    private static readonly Regex VersionPattern = new(@"^\s*v?(\d+)(\.(\d+))?(\.(\d+))?(\.(\d+))?(\+.*)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 比较两个版本号字符串：
    /// 返回 &lt; 0 表示 first &lt; second；
    /// 返回 0 表示版本相等（例如 1.2 与 1.2.0、1.2.0.0 与 1.2.0+abc 相等）；
    /// 返回 &gt; 0 表示 first &gt; second。
    /// </summary>
    public static int CompareVersions(string? first, string? second)
    {
        if (first == null && second == null) return 0;
        if (first == null) return -1;
        if (second == null) return 1;

        var v1 = Parse(first);
        var v2 = Parse(second);

        return v1.CompareTo(v2);
    }

    /// <summary>
    /// 校验版本号字符串是否为合法的数字版本格式
    /// </summary>
    public static bool IsValid(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var clean = version.Trim().TrimStart('v', 'V').Split('+')[0];
        return Regex.IsMatch(clean, @"^\d+(\.\d+){1,3}$");
    }

    /// <summary>
    /// 解析版本字符串为标准四段式 Version 对象（缺省段补 0）
    /// </summary>
    public static Version Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Version(0, 0, 0, 0);
        }

        var cleaned = text.Trim().TrimStart('v', 'V').Split('+')[0];

        if (Version.TryParse(cleaned, out var parsed))
        {
            return new Version(
                parsed.Major,
                Math.Max(0, parsed.Minor),
                Math.Max(0, parsed.Build),
                Math.Max(0, parsed.Revision));
        }

        // 若直接解析失败，逐段提取数字
        var parts = cleaned.Split('.');
        int major = parts.Length > 0 && int.TryParse(parts[0], out int p0) ? p0 : 0;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out int p1) ? p1 : 0;
        int build = parts.Length > 2 && int.TryParse(parts[2], out int p2) ? p2 : 0;
        int rev = parts.Length > 3 && int.TryParse(parts[3], out int p3) ? p3 : 0;

        return new Version(major, minor, build, rev);
    }
}
