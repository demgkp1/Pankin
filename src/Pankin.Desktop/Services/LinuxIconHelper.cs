using System;
using System.Collections.Concurrent;
using System.IO;
using Avalonia.Media.Imaging;

namespace Pankin.Desktop.Services;

public static class LinuxIconHelper
{
    private static readonly ConcurrentDictionary<string, Bitmap?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] GradientColors =
    [
        "#10B981", // 绿色
        "#3B82F6", // 蓝色
        "#6366F1", // 靛蓝
        "#8B5CF6", // 紫色
        "#EC4899", // 粉色
        "#EF4444", // 红色
        "#F59E0B", // 琥珀黄
        "#06B6D4"  // 青色
    ];

    public static string GetGradientColor(string name)
    {
        if (string.IsNullOrEmpty(name)) return GradientColors[0];
        int hash = Math.Abs(name.GetHashCode());
        return GradientColors[hash % GradientColors.Length];
    }

    public static string GetGlyphChar(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "✦";
        var trimmed = name.Trim();
        foreach (var rune in trimmed.EnumerateRunes())
        {
            if (!System.Text.Rune.IsWhiteSpace(rune))
            {
                return rune.ToString().ToUpperInvariant();
            }
        }
        return "✦";
    }

    public static Bitmap? TryLoadIcon(string? iconName)
    {
        if (string.IsNullOrWhiteSpace(iconName)) return null;

        if (IconCache.TryGetValue(iconName, out var cached))
        {
            return cached;
        }

        Bitmap? loaded = LoadIconInternal(iconName);
        IconCache[iconName] = loaded;
        return loaded;
    }

    private static Bitmap? LoadIconInternal(string iconName)
    {
        try
        {
            // 1. 若本身即为绝对路径
            if (File.Exists(iconName))
            {
                return new Bitmap(iconName);
            }

            // 2. 候选系统图标目录
            var candidatePaths = new[]
            {
                $"/usr/share/pixmaps/{iconName}.png",
                $"/usr/share/pixmaps/{iconName}.xpm",
                $"/usr/share/icons/hicolor/48x48/apps/{iconName}.png",
                $"/usr/share/icons/hicolor/64x64/apps/{iconName}.png",
                $"/usr/share/icons/hicolor/32x32/apps/{iconName}.png",
                $"/usr/share/icons/hicolor/128x128/apps/{iconName}.png",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $".local/share/icons/hicolor/48x48/apps/{iconName}.png")
            };

            foreach (var path in candidatePaths)
            {
                if (File.Exists(path))
                {
                    return new Bitmap(path);
                }
            }
        }
        catch
        {
            // 忽略格式不兼容或缺失情况
        }

        return null;
    }
}
