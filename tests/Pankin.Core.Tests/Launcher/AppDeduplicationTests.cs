using Pankin.Core.Abstractions;
using Pankin.Core.Launcher;
using Xunit;

namespace Pankin.Core.Tests.Launcher;

public class AppDeduplicationTests
{
    [Fact]
    public void DeduplicateApps_ShouldPreferUserLocalOverSystem()
    {
        var apps = new List<AppEntry>
        {
            new("org.gnome.Terminal", "Terminal", "终端", "/usr/bin/gnome-terminal", null, "/usr/share/applications/org.gnome.Terminal.desktop"),
            new("org.gnome.Terminal", "Terminal", "终端", "/home/user/.local/bin/gnome-terminal", null, "/home/user/.local/share/applications/org.gnome.Terminal.desktop")
        };

        var deduped = AppDeduplication.DeduplicateApps(apps);

        Assert.Single(deduped);
        Assert.Contains(".local/share", deduped[0].DesktopFilePath);
    }

    [Fact]
    public void DeduplicateApps_ShouldDeduplicateSameExecAndName()
    {
        var apps = new List<AppEntry>
        {
            new("code-system", "VS Code", "Visual Studio Code", "/usr/bin/code --unity-launch %F", null, "/usr/share/applications/code.desktop"),
            new("code-flatpak", "VS Code", "Visual Studio Code", "code", null, "/var/lib/flatpak/exports/share/applications/com.visualstudio.code.desktop")
        };

        var deduped = AppDeduplication.DeduplicateApps(apps);

        Assert.Single(deduped);
    }

    [Fact]
    public void GenerateStableId_ShouldBeDeterministic()
    {
        var app1 = new AppEntry("org.gnome.Nautilus", "Files", "文件", "nautilus --new-window %U", null, "/usr/share/applications/org.gnome.Nautilus.desktop");
        var app2 = new AppEntry("org.gnome.Nautilus", "Files", "文件", "nautilus", null, "/usr/share/applications/org.gnome.Nautilus.desktop");

        var id1 = AppDeduplication.GenerateStableId(app1);
        var id2 = AppDeduplication.GenerateStableId(app2);

        Assert.StartsWith("app:", id1);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void MergeUsage_ShouldAggregateCountAndPreserveLatestTime()
    {
        var t1 = DateTimeOffset.UtcNow.AddHours(-5);
        var t2 = DateTimeOffset.UtcNow.AddHours(-1);

        var usage = new Dictionary<string, AppUsage>
        {
            ["old-app-id"] = new AppUsage(5, t1),
            ["new-app-id"] = new AppUsage(10, t2)
        };

        var aliases = new Dictionary<string, string>
        {
            ["old-app-id"] = "new-app-id"
        };

        bool changed = AppDeduplication.MergeUsage(usage, aliases);

        Assert.True(changed);
        Assert.False(usage.ContainsKey("old-app-id"));
        Assert.True(usage.TryGetValue("new-app-id", out var merged));
        Assert.Equal(15, merged.Count);
        Assert.Equal(t2, merged.LastUsed);
    }

    [Fact]
    public void CalculateScore_ShouldApplyFrequencyAndRecencyBoost()
    {
        var now = DateTimeOffset.UtcNow;
        var recentUsage = new AppUsage(15, now.AddHours(-2));
        var oldUsage = new AppUsage(15, now.AddDays(-40));

        int baseScore = 90;
        int scoreWithoutUsage = AppDeduplication.CalculateScore(baseScore, null, now);
        int scoreWithOldUsage = AppDeduplication.CalculateScore(baseScore, oldUsage, now);
        int scoreWithRecentUsage = AppDeduplication.CalculateScore(baseScore, recentUsage, now);

        Assert.Equal(900, scoreWithoutUsage);
        Assert.True(scoreWithOldUsage > scoreWithoutUsage);
        Assert.True(scoreWithRecentUsage > scoreWithOldUsage);
    }
}
