using System.Globalization;
using System.Text.RegularExpressions;

namespace Pankin.Core.Launcher;

public sealed record ParsedPowerTimer(PowerAction Action, DateTimeOffset At, double? Minutes);

public static class PowerTimerParser
{
    private const string Act = "(关机|重启|睡眠|gj|cq|sm|shutdown|restart|sleep)";
    private const string Num = @"(\d+(?:\.\d+)?|十[一二]|[一两二三四五六七八九十半])";
    private const string Unit = "(分钟|分|min|m|个小时|小时|h)";

    private static readonly Dictionary<string, double> Numbers = new(StringComparer.Ordinal)
    {
        ["一"] = 1,
        ["两"] = 2,
        ["二"] = 2,
        ["三"] = 3,
        ["四"] = 4,
        ["五"] = 5,
        ["六"] = 6,
        ["七"] = 7,
        ["八"] = 8,
        ["九"] = 9,
        ["十"] = 10,
        ["十一"] = 11,
        ["十二"] = 12,
        ["半"] = 0.5
    };

    public static PowerAction Action(string word) => word.Trim().ToLowerInvariant() switch
    {
        "关机" or "gj" or "shutdown" => PowerAction.Shutdown,
        "重启" or "cq" or "restart" => PowerAction.Restart,
        _ => PowerAction.Sleep
    };

    private static Match Match(string s, string pattern) =>
        Regex.Match(s, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static ParsedPowerTimer? Parse(string input, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var s = input.Trim().ToLowerInvariant().Replace('：', ':');
        Match r;

        // 1. "30分钟后关机" 或 "1.5小时后重启"
        if ((r = Match(s, $@"^{Num}\s*{Unit}\s*后?\s*{Act}$")).Success)
            return Relative(r.Groups[3].Value, Number(r.Groups[1].Value), r.Groups[2].Value, now);

        // 2. "下午5点关机" 或 "18:30 重启"
        if ((r = Match(s, $@"^(上午|下午|晚上)?\s*(\d{{1,2}})(?::(\d{{2}})|点(半|\d{{1,2}})?分?)\s*{Act}$")).Success)
            return Absolute(r.Groups[5].Value, r.Groups[2].Value, r.Groups[3].Value, r.Groups[4].Value, r.Groups[1].Value, now);

        // 3. "关机 30" 或 "restart 10m"
        if ((r = Match(s, $@"^{Act}\s*(\d+(?:\.\d+)?)\s*{Unit}?$")).Success)
            return Relative(r.Groups[1].Value, Number(r.Groups[2].Value), r.Groups[3].Value, now);

        // 4. "关机 18:30" 或 "重启 9点"
        if ((r = Match(s, $@"^{Act}\s*(\d{{1,2}})(?::(\d{{2}})|点(半|\d{{1,2}})?)$")).Success)
            return Absolute(r.Groups[1].Value, r.Groups[2].Value, r.Groups[3].Value, r.Groups[4].Value, "", now);

        return null;
    }

    private static double Number(string s) =>
        Numbers.TryGetValue(s, out var n) ? n : double.Parse(s, CultureInfo.InvariantCulture);

    private static ParsedPowerTimer? Relative(string action, double n, string unit, DateTimeOffset now)
    {
        var minutes = unit is "个小时" or "小时" or "h" ? n * 60 : n;
        return minutes >= 1 && minutes <= 1440
            ? new(Action(action), now.AddMinutes(minutes), minutes)
            : null;
    }

    private static ParsedPowerTimer? Absolute(string action, string hour, string minute, string point, string part, DateTimeOffset now)
    {
        if (!int.TryParse(hour, CultureInfo.InvariantCulture, out int h)) return null;

        int m = minute.Length > 0
            ? int.Parse(minute, CultureInfo.InvariantCulture)
            : point == "半" ? 30
            : point.Length > 0 ? int.Parse(point, CultureInfo.InvariantCulture)
            : 0;

        if ((part is "下午" or "晚上") && h < 12) h += 12;
        if (h > 23 || m > 59) return null;

        var at = new DateTimeOffset(now.Year, now.Month, now.Day, h, m, 0, now.Offset);
        if (at <= now) at = at.AddDays(1);

        return (at - now) >= TimeSpan.FromMinutes(1) && (at - now) <= TimeSpan.FromHours(24)
            ? new(Action(action), at, null)
            : null;
    }
}

/// <summary>
/// 自然语言定时关机/重启/睡眠搜索来源
/// </summary>
public sealed class PowerTimerSource : LauncherSource
{
    private readonly Func<IPowerTimer?> _provider;
    private readonly TimeProvider _clock;

    public PowerTimerSource(Func<IPowerTimer?> provider, TimeProvider? clock = null)
        : base(LauncherGroups.Timer, -10, "power:")
    {
        _provider = provider;
        _clock = clock ?? TimeProvider.System;
    }

    public override int Limit(SourceContext context) => 2;

    private static string ActionName(PowerAction action) => action switch
    {
        PowerAction.Shutdown => "关机",
        PowerAction.Restart => "重启",
        _ => "睡眠"
    };

    private static string FormatSpan(double minutes)
    {
        var h = (int)Math.Floor(minutes / 60);
        var m = (int)Math.Floor(minutes % 60 + 0.5);
        if (h == 0) return $"{m} 分钟";
        if (m == 0) return $"{h} 小时";
        return $"{h} 小时 {m} 分钟";
    }

    private static string FormatClock(DateTimeOffset at, DateTimeOffset now) =>
        at.Date > now.Date
            ? $"明天 {at:HH:mm}"
            : at.ToString("HH:mm", CultureInfo.InvariantCulture);

    public override Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var timer = _provider();
        if (timer == null) return Rows([]);

        var query = context.Query.Trim().ToLowerInvariant();
        if (query.Length == 0) return Rows([]);

        var now = _clock.GetLocalNow();
        var state = timer.Current;
        var result = new List<LauncherItem>();

        var parsed = PowerTimerParser.Parse(query, now);
        if (parsed != null)
        {
            var actionStr = parsed.Action.ToString().ToLowerInvariant();
            var atStr = FormatClock(parsed.At, now);
            var title = parsed.Minutes is { } mins
                ? $"{FormatSpan(mins)}后{ActionName(parsed.Action)}"
                : $"{atStr} {ActionName(parsed.Action)}";

            var subtitle = state == null
                ? $"到点自动{ActionName(parsed.Action)}"
                : $"替换现有定时: {FormatClock(state.At, now)} {ActionName(state.Action)}";

            var glyph = parsed.Action switch
            {
                PowerAction.Shutdown => "power",
                PowerAction.Restart => "rotate-cw",
                _ => "moon"
            };

            result.Add(new LauncherItem(
                $"power:{actionStr}@{parsed.At.ToUnixTimeSeconds()}",
                LauncherItemKind.Command,
                Group,
                title,
                subtitle,
                new LauncherIcon(glyph),
                Score: 100));
        }

        string[] cancelKeywords = ["取消定时", "取消关机", "qxds", "取消重启", "取消睡眠"];
        if (state != null && query.Length >= 2 && cancelKeywords.Any(k => k.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
        {
            var remaining = Math.Ceiling(Math.Max(0, (state.At - now).TotalMinutes));
            result.Add(new LauncherItem(
                "power:cancel",
                LauncherItemKind.Command,
                Group,
                $"取消定时{ActionName(state.Action)}",
                $"当前设定: {FormatClock(state.At, now)} (剩余约 {FormatSpan(remaining)})",
                new LauncherIcon("x"),
                Score: 99));
        }

        string[] panelKeywords = ["定时关机", "定时", "dsgj", "ds"];
        if (parsed == null && panelKeywords.Any(k => string.Equals(k, query, StringComparison.OrdinalIgnoreCase) || (query.Length >= 2 && k.StartsWith(query, StringComparison.OrdinalIgnoreCase))))
        {
            var subtitle = state == null
                ? "输入例如「30分钟后关机」或「22:00 重启」"
                : $"当前定时: {FormatClock(state.At, now)} {ActionName(state.Action)}";

            result.Add(new LauncherItem(
                "power:panel",
                LauncherItemKind.Command,
                Group,
                "定时电源计划",
                subtitle,
                new LauncherIcon("clock"),
                Score: 90));
        }

        return Rows(result);
    }

    public override Task ExecuteAsync(LauncherItem item, SourceContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var timer = _provider();
        if (timer == null || item.Id == "power:panel") return Task.CompletedTask;

        if (item.Id == "power:cancel")
        {
            timer.Cancel();
            return Task.CompletedTask;
        }

        var parts = item.Id[6..].Split('@');
        if (parts.Length != 2 || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out var seconds))
            return Task.CompletedTask;

        PowerAction? action = parts[0] switch
        {
            "shutdown" => PowerAction.Shutdown,
            "restart" => PowerAction.Restart,
            "sleep" => PowerAction.Sleep,
            _ => null
        };

        if (action.HasValue)
        {
            var at = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
            var earliest = _clock.GetLocalNow().AddMinutes(1);
            timer.Schedule(action.Value, at < earliest ? earliest : at);
        }

        return Task.CompletedTask;
    }
}
