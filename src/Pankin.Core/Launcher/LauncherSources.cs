using Pankin.Core.Abstractions;
using Pankin.Core.Settings;

namespace Pankin.Core.Launcher;

/// <summary>
/// 本地已安装桌面应用搜索来源 (基于 PinyinMatcher 与使用频率/近期使用增益)
/// </summary>
public sealed class ApplicationSource : LauncherSource
{
    private readonly IAppScanner _appScanner;
    private readonly ILauncherActionHost _actionHost;
    private readonly Action<string> _recordUsage;

    public ApplicationSource(IAppScanner appScanner, ILauncherActionHost actionHost, Action<string> recordUsage)
        : base(LauncherGroups.Applications, 1, "app:")
    {
        _appScanner = appScanner;
        _actionHost = actionHost;
        _recordUsage = recordUsage;
    }

    public override int Limit(SourceContext context) =>
        context.Settings.MaxAppRows * 7; // 默认网格每行 7 个，最多 MaxAppRows 行

    public override bool Owns(LauncherItem item) => item.Kind == LauncherItemKind.App;

    public override Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;

        if (context.Query.Length == 0)
        {
            // 首页空搜索：展示近期使用应用 + 已固定应用
            var homeItems = new List<LauncherItem>();
            var recentApps = context.Apps
                .Where(a => context.Usage.ContainsKey(a.Id))
                .OrderByDescending(a => context.Usage[a.Id].LastUsed)
                .Take(8)
                .Select(a => ToLauncherItem(a, LauncherGroups.Recent, 100));

            homeItems.AddRange(recentApps);

            var pinnedIds = new HashSet<string>(context.Settings.PinnedAppIds, StringComparer.OrdinalIgnoreCase);
            var pinnedApps = context.Apps
                .Where(a => pinnedIds.Contains(a.Id) && !homeItems.Any(h => h.Id == a.Id))
                .Select(a => ToLauncherItem(a, LauncherGroups.Pinned, 90));

            homeItems.AddRange(pinnedApps);
            return Rows(homeItems);
        }

        var matcher = PinyinMatcher.Instance;
        var query = context.Query.Trim();
        var hits = new List<(AppEntry App, NameMatch Match, int TotalScore)>();

        foreach (var app in context.Apps)
        {
            token.ThrowIfCancellationRequested();

            // 优先匹配本地化显示名称，次选匹配原始名称，再选匹配执行文件名
            var match = matcher.Match(app.DisplayName, query)
                        ?? matcher.Match(app.Name, query);

            if (match == null)
            {
                var binName = AppDeduplication.NormalizeExec(app.Exec);
                if (binName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                {
                    match = new NameMatch(80, []);
                }
                else if (binName.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    match = new NameMatch(60, []);
                }
            }

            if (match != null)
            {
                var usage = context.Usage.GetValueOrDefault(app.Id);
                int totalScore = AppDeduplication.CalculateScore(match.Score, usage, now);
                hits.Add((app, match, totalScore));
            }
        }

        var results = hits
            .OrderByDescending(x => x.TotalScore)
            .ThenByDescending(x => context.Usage.GetValueOrDefault(x.App.Id)?.Count ?? 0)
            .ThenByDescending(x => context.Usage.GetValueOrDefault(x.App.Id)?.LastUsed ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.App.DisplayName.Length)
            .ThenBy(x => x.App.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(Limit(context))
            .Select(x => ToLauncherItem(x.App, Group, x.TotalScore, x.Match.Spans))
            .ToList();

        return Rows(results);
    }

    private static LauncherItem ToLauncherItem(
        AppEntry app,
        string group,
        int score,
        IReadOnlyList<TextSpan>? highlights = null)
    {
        return new LauncherItem(
            app.Id,
            LauncherItemKind.App,
            group,
            app.DisplayName,
            app.Exec,
            new LauncherIcon("app", app.Icon),
            Highlights: highlights,
            Score: score,
            Tag: app);
    }

    public override async Task ExecuteAsync(LauncherItem item, SourceContext context, CancellationToken token)
    {
        var app = context.Apps.FirstOrDefault(a => a.Id == item.Id) ?? (item.Tag as AppEntry);
        if (app != null)
        {
            await _appScanner.LaunchAppAsync(app, token).ConfigureAwait(false);
            _recordUsage(app.Id);
        }
        else
        {
            await _actionHost.OpenAsync(item.Subtitle ?? item.Title, false, token).ConfigureAwait(false);
            _recordUsage(item.Id);
        }
    }
}

/// <summary>
/// 纯数学表达式算式计算来源
/// </summary>
public sealed class CalculationSource : LauncherSource
{
    private readonly ILauncherActionHost _actionHost;

    public CalculationSource(ILauncherActionHost actionHost)
        : base(LauncherGroups.Calculation, 0, "calc:")
    {
        _actionHost = actionHost;
    }

    public override int Limit(SourceContext context) => 1;

    public override Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (CalculatorService.TryEvaluate(context.Query, out var result, out _))
        {
            var item = new LauncherItem(
                "calc:" + result,
                LauncherItemKind.Calculation,
                Group,
                result,
                $"{context.Query} =",
                new LauncherIcon("equal"),
                Score: 100);

            return Rows([item]);
        }

        return Rows([]);
    }

    public override Task ExecuteAsync(LauncherItem item, SourceContext context, CancellationToken token)
    {
        var resultText = item.Id.StartsWith("calc:", StringComparison.Ordinal)
            ? item.Id[5..]
            : item.Title;

        return _actionHost.CopyTextAsync(resultText, token);
    }
}

/// <summary>
/// 本地文件快速检索来源 (基于 IFileSearcher / plocate 流式检索)
/// </summary>
public sealed class FileSource : LauncherSource
{
    private readonly IFileSearcher _fileSearcher;
    private readonly ILauncherActionHost _actionHost;

    public FileSource(IFileSearcher fileSearcher, ILauncherActionHost actionHost)
        : base(LauncherGroups.Files, 3, "file:")
    {
        _fileSearcher = fileSearcher;
        _actionHost = actionHost;
    }

    public override int Limit(SourceContext context) =>
        context.IsFileExplicit ? Math.Max(context.Settings.MaxFileRows, 20) : context.Settings.MaxFileRows;

    public override TimeSpan Timeout => TimeSpan.FromMilliseconds(1200);

    public override async Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!LauncherSearch.IsValidFileQuery(context.Query, context.IsFileExplicit) || context.Query.StartsWith('>'))
            return Array.Empty<LauncherItem>();

        var limit = Limit(context);
        var fetchLimit = Math.Max(limit * 10, 100);
        var results = new List<LauncherItem>();

        var query = context.Query;
        if (query.StartsWith("f ", StringComparison.OrdinalIgnoreCase))
        {
            var stripped = query[2..].Trim();
            if (!string.IsNullOrEmpty(stripped))
            {
                query = stripped;
            }
        }

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(Timeout);

            await foreach (var file in _fileSearcher.SearchFilesAsync(query, fetchLimit, timeoutCts.Token).ConfigureAwait(false))
            {
                var kind = file.IsDirectory ? LauncherItemKind.Folder : LauncherItemKind.File;
                var glyph = file.IsDirectory ? "folder" : "file";

                int score = 0;

                // 路径权重
                if (!string.IsNullOrEmpty(homeDir) && file.FullPath.StartsWith(homeDir, StringComparison.Ordinal))
                {
                    var relPath = file.FullPath[homeDir.Length..];
                    // 仅对明确的缓存、git 与构建产物目录降权，保留 .gemini/.claude/.cursor/.config 等工作区与配置目录
                    bool isNoisePath = relPath.Contains("/.cache/", StringComparison.OrdinalIgnoreCase) ||
                                       relPath.EndsWith("/.cache", StringComparison.OrdinalIgnoreCase) ||
                                       relPath.Contains("/.local/", StringComparison.OrdinalIgnoreCase) ||
                                       relPath.EndsWith("/.local", StringComparison.OrdinalIgnoreCase) ||
                                       relPath.Contains("/.git/", StringComparison.OrdinalIgnoreCase) ||
                                       relPath.EndsWith("/.git", StringComparison.OrdinalIgnoreCase) ||
                                       relPath.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase) ||
                                       relPath.EndsWith("/node_modules", StringComparison.OrdinalIgnoreCase) ||
                                       relPath.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                                       relPath.EndsWith("/obj", StringComparison.OrdinalIgnoreCase) ||
                                       relPath.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                                       relPath.EndsWith("/bin", StringComparison.OrdinalIgnoreCase) ||
                                       relPath.Contains("/target/", StringComparison.OrdinalIgnoreCase) ||
                                       relPath.EndsWith("/target", StringComparison.OrdinalIgnoreCase);

                    score += isNoisePath ? 20 : 100;
                }
                else if (file.FullPath.StartsWith("/usr", StringComparison.Ordinal) ||
                         file.FullPath.StartsWith("/etc", StringComparison.Ordinal) ||
                         file.FullPath.StartsWith("/var", StringComparison.Ordinal) ||
                         file.FullPath.StartsWith("/snap", StringComparison.Ordinal) ||
                         file.FullPath.StartsWith("/lib", StringComparison.Ordinal))
                {
                    score += 0;
                }
                else
                {
                    score += 10;
                }

                // 名称匹配度权重（对 file.FileName 和 query = context.Query，不区分大小写）
                if (file.FileName.Equals(query, StringComparison.OrdinalIgnoreCase))
                {
                    score += 80;
                }
                else if (file.FileName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                {
                    score += 50;
                }
                else if (file.FileName.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    score += 30;
                }
                else if (ContainsChinese(file.FileName) && PinyinMatcher.Instance.Match(file.FileName, query) != null)
                {
                    score += 25;
                }

                // 类型与长度微调
                if (file.IsDirectory)
                {
                    score += 10;
                }

                results.Add(new LauncherItem(
                    "file:" + file.FullPath,
                    kind,
                    Group,
                    file.FileName,
                    file.DirectoryPath,
                    new LauncherIcon(glyph),
                    Score: score));

                if (results.Count >= fetchLimit) break;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // 超时截断，返回已检索到的内容
        }
        catch
        {
            // 忽略检索异常
        }

        return results
            .OrderByDescending(i => i.Score)
            .ThenBy(i => i.Title.Length)
            .ThenBy(i => i.Title)
            .Take(limit)
            .ToList();
    }

    public override Task ExecuteAsync(LauncherItem item, SourceContext context, CancellationToken token)
    {
        var path = item.Id.StartsWith("file:", StringComparison.Ordinal)
            ? item.Id[5..]
            : item.Title;

        return _actionHost.OpenAsync(path, false, token);
    }

    private static bool ContainsChinese(string text)
    {
        foreach (char c in text)
        {
            if (c >= '\u4e00' && c <= '\u9fff') return true;
        }
        return false;
    }
}

/// <summary>
/// 系统控制指令来源 (锁屏、睡眠、重启、关机、注销、清空废纸篓)
/// </summary>
public sealed class ExtraCommandSource : LauncherSource
{
    private sealed record CommandDefinition(string Cmd, string Title, string Subtitle, string Glyph, string[] Keywords);

    private static readonly CommandDefinition[] Commands =
    [
        new("lock", "锁定屏幕", "立即锁定当前桌面会话", "lock", ["lock", "suoding", "sd", "锁屏"]),
        new("sleep", "系统睡眠", "挂起并进入低功耗待机模式", "moon", ["sleep", "suspend", "shuimian", "sm", "睡眠", "待机"]),
        new("restart", "重启计算机", "重新启动操作系统", "rotate-cw", ["restart", "reboot", "chongqi", "cq", "重启"]),
        new("shutdown", "关闭计算机", "安全关闭计算机系统电源", "power", ["shutdown", "poweroff", "guanji", "gj", "关机"]),
        new("logout", "注销当前用户", "退出当前登录的桌面会话", "log-out", ["logout", "logoff", "zhuxiao", "zx", "注销", "退出登录"]),
        new("recycle", "清空回收站", "永久删除回收站/废纸篓中的所有项目", "trash-2", ["trash", "recycle", "huishouzhan", "hsz", "清空回收站", "清空废纸篓"])
    ];

    private readonly ILauncherActionHost _actionHost;

    public ExtraCommandSource(ILauncherActionHost actionHost)
        : base(LauncherGroups.Commands, 4, "command:")
    {
        _actionHost = actionHost;
    }

    public override int Limit(SourceContext context) => 3;

    public override Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (context.Query.Length == 0) return Rows([]);

        var query = context.Query.Trim().ToLowerInvariant();
        var matcher = PinyinMatcher.Instance;
        var hits = new List<LauncherItem>();

        foreach (var def in Commands)
        {
            var match = matcher.Match(def.Title, query);
            bool keywordMatch = def.Keywords.Any(k => k.StartsWith(query, StringComparison.OrdinalIgnoreCase) || k.Contains(query, StringComparison.OrdinalIgnoreCase));

            if (match != null || keywordMatch)
            {
                hits.Add(new LauncherItem(
                    "command:" + def.Cmd,
                    LauncherItemKind.Command,
                    Group,
                    def.Title,
                    def.Subtitle,
                    new LauncherIcon(def.Glyph),
                    Highlights: match?.Spans,
                    Score: match?.Score ?? 80));
            }
        }

        return Rows(hits.OrderByDescending(h => h.Score).Take(Limit(context)));
    }

    public override Task ExecuteAsync(LauncherItem item, SourceContext context, CancellationToken token)
    {
        var cmd = item.Id.StartsWith("command:", StringComparison.Ordinal)
            ? item.Id[8..]
            : item.Id;

        return _actionHost.ExecuteSystemCommandAsync(cmd, token);
    }
}

/// <summary>
/// 网页搜索引擎快捷直达搜索来源
/// </summary>
public sealed class WebSource : LauncherSource
{
    private static readonly SearchEngine[] PresetEngines =
    [
        new("bing", "Bing", "https://www.bing.com/search?q={query}"),
        new("baidu", "Baidu", "https://www.baidu.com/s?wd={query}"),
        new("google", "Google", "https://www.google.com/search?q={query}"),
        new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q={query}"),
        new("gh", "GitHub", "https://github.com/search?q={query}")
    ];

    private readonly ILauncherActionHost _actionHost;

    public WebSource(ILauncherActionHost actionHost)
        : base(LauncherGroups.Web, 6, "web:")
    {
        _actionHost = actionHost;
    }

    public override int Limit(SourceContext context) => 2;

    public override Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (context.Query.Length == 0 || context.Query.StartsWith('>'))
            return Rows([]);

        var query = context.Query.Trim();
        var list = new List<LauncherItem>();

        // 判断是否有搜索引擎关键词前缀 (例如 "bing hello" 或 "gh avalonia")
        int spaceIndex = query.IndexOf(' ');
        if (spaceIndex > 0)
        {
            var prefix = query[..spaceIndex];
            var searchPayload = query[(spaceIndex + 1)..].Trim();
            var engine = PresetEngines.FirstOrDefault(e => string.Equals(e.Keyword, prefix, StringComparison.OrdinalIgnoreCase));
            if (engine != null && !string.IsNullOrEmpty(searchPayload))
            {
                var url = engine.UrlTemplate.Replace("{query}", Uri.EscapeDataString(searchPayload), StringComparison.Ordinal);
                list.Add(new LauncherItem(
                    "web:" + url,
                    LauncherItemKind.Web,
                    Group,
                    $"在 {engine.Name} 中搜索「{searchPayload}」",
                    engine.Name,
                    new LauncherIcon("globe"),
                    Score: 30));
            }
        }

        // 默认搜索引擎兜底选项
        var defaultEngine = PresetEngines.FirstOrDefault(e => string.Equals(e.Keyword, context.Settings.DefaultSearchEngine, StringComparison.OrdinalIgnoreCase))
                            ?? PresetEngines[0];

        var defaultUrl = defaultEngine.UrlTemplate.Replace("{query}", Uri.EscapeDataString(query), StringComparison.Ordinal);
        list.Add(new LauncherItem(
            "web:" + defaultUrl,
            LauncherItemKind.Web,
            Group,
            $"在 {defaultEngine.Name} 中搜索「{query}」",
            defaultEngine.Name,
            new LauncherIcon("search"),
            Score: 10));

        return Rows(list.Take(Limit(context)));
    }

    public override Task ExecuteAsync(LauncherItem item, SourceContext context, CancellationToken token)
    {
        var url = item.Id.StartsWith("web:", StringComparison.Ordinal)
            ? item.Id[4..]
            : item.Id;

        return _actionHost.OpenAsync(url, false, token);
    }
}
