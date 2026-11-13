using System;
using System.Collections.Concurrent;
using System.IO;
using Avalonia.Media.Imaging;

namespace Pankin.Desktop.Services;

public static class LinuxIconHelper
{
    private static readonly ConcurrentDictionary<string, Lazy<Bitmap?>> IconCache = new(StringComparer.Ordinal);

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
        int hash = name.GetHashCode() & int.MaxValue;
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

        return IconCache.GetOrAdd(iconName, name => new Lazy<Bitmap?>(() => LoadIconInternal(name))).Value;
    }

    private static Bitmap? LoadIconInternal(string iconName)
    {
        try
        {
            // Platform decoding includes XDG inheritance and SVG/PNG/XPM. Rasterize at
            // 96 px so both the 36-DIP grid and the 28-DIP rows stay crisp at 200%.
            var png = DesktopAppContext.Instance.AppScanner.LoadIconPng(iconName, 96);
            if (png == null) return null;
            using var stream = new MemoryStream(png, writable: false);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return null;
        }
    }
}
