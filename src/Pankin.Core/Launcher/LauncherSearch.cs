using System.Text.Json;
using Pankin.Core.Abstractions;
using Pankin.Core.FolderJump;
using Pankin.Core.Settings;

namespace Pankin.Core.Launcher;

/// <summary>
/// 跨平台多来源启动器检索聚合主干
/// 注意：Windows 专用的 SystemSettingsSource (ms-settings:) 与 PortableApps/Catalog
/// 在 Linux 原生重构中已按设计跳过 (Skipped)。
/// </summary>
public sealed class LauncherSearch : ILauncherSearch, ILauncherItemActions, IDisposable
{
    private readonly IAppScanner _appScanner;
    private readonly IFileSearcher _fileSearcher;
    private readonly ILauncherActionHost _actionHost;
    private readonly SettingsStore _settingsStore;
    private readonly TerminalService _terminalService;
    private readonly IWebSuggestions? _webSuggestions;
    private readonly Func<IPowerTimer?> _powerTimerProvider;
    private readonly TimeProvider _clock;
    private readonly FolderJumpStore _folderJumpStore;

    private readonly object _lock = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly string _usageFilePath;

    private Dictionary<string, AppUsage> _usage = [];
    private IReadOnlyList<AppEntry> _cachedApps = [];
    private Task _initialScanTask = Task.CompletedTask;
    private bool _disposed;

    private readonly ILauncherSource[] _sources;

    public ITerminalHistory TerminalHistory => _terminalService.History;
    public IReadOnlyList<AppEntry> CachedApps => _cachedApps;

    public LauncherSearch(
        IAppScanner appScanner,
        IFileSearcher fileSearcher,
        ILauncherActionHost actionHost,
        SettingsStore settingsStore,
        IPowerTimer? powerTimer = null,
        IWebSuggestions? webSuggestions = null,
        TimeProvider? clock = null,
        FolderJumpStore? folderJumpStore = null)
    {
        _appScanner = appScanner;
        _fileSearcher = fileSearcher;
        _actionHost = actionHost;
        _settingsStore = settingsStore;
        _webSuggestions = webSuggestions;
        _clock = clock ?? TimeProvider.System;
        _powerTimerProvider = () => powerTimer;
        _folderJumpStore = folderJumpStore ?? new FolderJumpStore();

        var configDir = Path.GetDirectoryName(settingsStore.ConfigFilePath)
                        ?? Path.GetDirectoryName(SettingsStore.GetDefaultConfigPath())!;
        _usageFilePath = Path.Combine(configDir, "launcher-usage.json");
        _terminalService = new TerminalService(_actionHost, configDir);

        _sources =
        [
            new CalculationSource(_actionHost),
            new PowerTimerSource(_powerTimerProvider, _clock),
            new ApplicationSource(_appScanner, _actionHost, RecordUsage),
            new FolderJumpLauncherSource(_folderJumpStore, _actionHost),
            new ExtraCommandSource(_actionHost),
            new FileSource(_fileSearcher, _actionHost),
            new WebSource(_actionHost)
        ];

        LoadUsage();
        RefreshApps();
    }

    private void LoadUsage()
    {
        try
        {
            if (File.Exists(_usageFilePath))
            {
                var json = File.ReadAllText(_usageFilePath);
                _usage = JsonSerializer.Deserialize(json, LauncherUsageJsonContext.Default.DictionaryStringAppUsage) ?? [];
            }
        }
        catch
        {
            _usage = [];
        }
    }

    private void SaveUsage()
    {
        try
        {
            var dir = Path.GetDirectoryName(_usageFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _usageFilePath + ".tmp";
            var json = JsonSerializer.Serialize(_usage, LauncherUsageJsonContext.Default.DictionaryStringAppUsage);
            File.WriteAllText(tmp, json);
            File.Move(tmp, _usageFilePath, true);
        }
        catch
        {
            // 忽略写入故障
        }
    }

    public void RecordUsage(string id)
    {
        lock (_lock)
        {
            var current = _usage.GetValueOrDefault(id);
            int newCount = Math.Min(int.MaxValue - 1, (current?.Count ?? 0) + 1);
            _usage[id] = new AppUsage(newCount, _clock.GetUtcNow());
            SaveUsage();
        }
    }

    public Task RefreshAppsAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_disposed) return Task.CompletedTask;
            _initialScanTask = Task.Run(async () =>
            {
                try
                {
                    var scanned = await _appScanner.ScanAppsAsync(ct).ConfigureAwait(false);
                    var deduped = AppDeduplication.DeduplicateApps(scanned);
                    lock (_lock)
                    {
                        _cachedApps = deduped;
                    }
                }
                catch
                {
                    // 容错处理
                }
            }, ct);
            return _initialScanTask;
        }
    }

    public void RefreshApps() => _ = RefreshAppsAsync(_lifetimeCts.Token);

    private SourceContext BuildContext(string query)
    {
        IReadOnlyList<AppEntry> apps;
        Dictionary<string, AppUsage> usageCopy;
        lock (_lock)
        {
            apps = _cachedApps;
            usageCopy = new Dictionary<string, AppUsage>(_usage, StringComparer.OrdinalIgnoreCase);
        }

        return new SourceContext(
            query.Trim(),
            apps,
            _settingsStore.Current.Launcher,
            usageCopy,
            _settingsStore.Current);
    }

    public async Task<LauncherResult> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (query.StartsWith('>'))
        {
            return _terminalService.Search(query);
        }

        var context = BuildContext(query);
        var tasks = _sources
            .OrderBy(s => s.Order)
            .Select(s => SearchSourceSafelyAsync(s, context, cancellationToken))
            .ToArray();

        var groups = await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var allItems = groups.SelectMany(g => g).ToList();
        int fileCount = allItems.Count(i => i.Kind is LauncherItemKind.File or LauncherItemKind.Folder);

        return new LauncherResult(allItems, context.Query.Length == 0, fileCount);
    }

    public async Task<LauncherResult> SearchAsync(
        string query,
        Action<LauncherResult> partial,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (query.StartsWith('>'))
        {
            var res = _terminalService.Search(query);
            partial(res);
            return res;
        }

        var context = BuildContext(query);

        // 快慢来源分离：本地轻量来源立即返回，远程/磁盘大扫描异步返回
        var fastSources = _sources.Where(s => s is not FileSource).ToArray();
        var slowSources = _sources.Where(s => s is FileSource).ToArray();

        var fastTasks = fastSources.Select(s => SearchSourceSafelyAsync(s, context, cancellationToken)).ToArray();
        var fastGroups = await Task.WhenAll(fastTasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var fastItems = fastGroups.SelectMany(g => g).ToList();
        if (fastItems.Count > 0 || context.Query.Length == 0)
        {
            partial(new LauncherResult(fastItems, context.Query.Length == 0, 0));
        }

        if (slowSources.Length == 0 || context.Query.Length < 2)
        {
            return new LauncherResult(fastItems, context.Query.Length == 0, 0);
        }

        var slowTasks = slowSources.Select(s => SearchSourceSafelyAsync(s, context, cancellationToken)).ToArray();
        var slowGroups = await Task.WhenAll(slowTasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var allItems = fastItems.Concat(slowGroups.SelectMany(g => g)).ToList();
        int fileCount = allItems.Count(i => i.Kind is LauncherItemKind.File or LauncherItemKind.Folder);

        var finalResult = new LauncherResult(allItems, context.Query.Length == 0, fileCount);
        return finalResult;
    }

    private static async Task<IReadOnlyList<LauncherItem>> SearchSourceSafelyAsync(
        ILauncherSource source,
        SourceContext context,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(source.Timeout);

        try
        {
            return await source.SearchAsync(context, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Array.Empty<LauncherItem>();
        }
        catch
        {
            return Array.Empty<LauncherItem>();
        }
    }

    public async Task ExecuteAsync(LauncherItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (item.Kind == LauncherItemKind.Folder)
        {
            var folderPath = item.Id.StartsWith("folder-jump:", StringComparison.Ordinal)
                ? item.Id[12..]
                : (item.Id.StartsWith("file:", StringComparison.Ordinal) ? item.Id[5..] : (item.Subtitle ?? item.Title));
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                _ = _folderJumpStore.RecordAccessAsync(folderPath, cancellationToken);
            }
        }

        if (TerminalService.Owns(item))
        {
            await _terminalService.ExecuteAsync(item, false, cancellationToken).ConfigureAwait(false);
            return;
        }

        var context = BuildContext("");
        var source = _sources.FirstOrDefault(s => s.Owns(item));
        if (source != null)
        {
            await source.ExecuteAsync(item, context, cancellationToken).ConfigureAwait(false);
            return;
        }

        // 默认按路径/URL打开
        await _actionHost.OpenAsync(item.Subtitle ?? item.Title, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExecuteElevatedAsync(LauncherItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (TerminalService.Owns(item))
        {
            await _terminalService.ExecuteAsync(item, true, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (item.Kind == LauncherItemKind.App)
        {
            var app = item.Tag as AppEntry ?? _cachedApps.FirstOrDefault(a => a.Id == item.Id);
            if (app != null)
            {
                await _actionHost.OpenAsync(app.Exec, true, cancellationToken).ConfigureAwait(false);
                RecordUsage(app.Id);
                return;
            }
        }

        await ExecuteAsync(item, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<LauncherItemAction>> GetActionsAsync(LauncherItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var actions = new List<LauncherItemAction>();

        if (item.Kind == LauncherItemKind.App)
        {
            actions.Add(LauncherItemAction.RunAsAdmin);
            actions.Add(LauncherItemAction.OpenLocation);
            actions.Add(LauncherItemAction.CopyPath);
            actions.Add(LauncherItemAction.Properties);
        }
        else if (item.Kind is LauncherItemKind.File or LauncherItemKind.Folder)
        {
            actions.Add(LauncherItemAction.OpenLocation);
            actions.Add(LauncherItemAction.CopyPath);
            actions.Add(LauncherItemAction.Properties);
        }

        return Task.FromResult<IReadOnlyList<LauncherItemAction>>(actions);
    }

    public async Task RunAsync(LauncherItem item, LauncherItemAction action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (action)
        {
            case LauncherItemAction.RunAsAdmin:
                await ExecuteElevatedAsync(item, cancellationToken).ConfigureAwait(false);
                break;

            case LauncherItemAction.OpenLocation:
                if (item.Kind == LauncherItemKind.App)
                {
                    var app = item.Tag as AppEntry ?? _cachedApps.FirstOrDefault(a => a.Id == item.Id);
                    if (app != null && !string.IsNullOrEmpty(app.DesktopFilePath))
                    {
                        await _actionHost.RevealAsync(app.DesktopFilePath, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (item.Kind is LauncherItemKind.File or LauncherItemKind.Folder)
                {
                    var path = item.Id.StartsWith("file:", StringComparison.Ordinal) ? item.Id[5..] : item.Subtitle ?? item.Title;
                    await _actionHost.RevealAsync(path, cancellationToken).ConfigureAwait(false);
                }
                break;

            case LauncherItemAction.CopyPath:
                if (item.Kind == LauncherItemKind.App)
                {
                    var app = item.Tag as AppEntry ?? _cachedApps.FirstOrDefault(a => a.Id == item.Id);
                    var target = app?.DesktopFilePath ?? app?.Exec ?? item.Subtitle ?? item.Title;
                    await _actionHost.CopyTextAsync(target, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var path = item.Id.StartsWith("file:", StringComparison.Ordinal) ? item.Id[5..] : item.Subtitle ?? item.Title;
                    await _actionHost.CopyTextAsync(path, cancellationToken).ConfigureAwait(false);
                }
                break;

            case LauncherItemAction.Properties:
                if (item.Kind == LauncherItemKind.App)
                {
                    var app = item.Tag as AppEntry ?? _cachedApps.FirstOrDefault(a => a.Id == item.Id);
                    if (app != null && !string.IsNullOrEmpty(app.DesktopFilePath))
                    {
                        await _actionHost.ShowPropertiesAsync(app.DesktopFilePath, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    var path = item.Id.StartsWith("file:", StringComparison.Ordinal) ? item.Id[5..] : item.Subtitle ?? item.Title;
                    await _actionHost.ShowPropertiesAsync(path, cancellationToken).ConfigureAwait(false);
                }
                break;

            default:
                throw new LauncherActionException($"不支持的操作: {action}");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        (_webSuggestions as IDisposable)?.Dispose();
    }
}
