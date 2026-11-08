using System;
using System.Threading;
using System.Threading.Tasks;
using Pankin.Core.Abstractions;
using Pankin.Core.Ai;
using Pankin.Core.Clipboard;
using Pankin.Core.FolderJump;
using Pankin.Core.Launcher;
using Pankin.Core.Modules;
using Pankin.Core.Rename;
using Pankin.Core.Settings;
using Pankin.Core.Setup;
using Pankin.Core.Updates;
using Pankin.Platform.Linux;

namespace Pankin.Desktop.Services;

/// <summary>
/// Pankin 桌面端全局服务上下文，统一组装 Core 业务层与 Linux 平台层服务
/// </summary>
public sealed class DesktopAppContext : IDisposable
{
    private static readonly Lazy<DesktopAppContext> LazyInstance = new(() => new DesktopAppContext());
    public static DesktopAppContext Instance => LazyInstance.Value;

    public SettingsStore SettingsStore { get; }
    public IAppScanner AppScanner { get; }
    public IFileSearcher FileSearcher { get; }
    public IClipboardService ClipboardService { get; }
    public ClipboardStorage ClipboardStorage { get; }
    public ClipboardHistoryManager ClipboardHistory { get; }
    public IAiService AiService { get; }
    public TranslationService TranslationService { get; }
    public IAutostartService AutostartService { get; }
    public IGlobalInputHook InputHook { get; }
    public ILauncherActionHost ActionHost { get; }
    public PowerTimerService PowerTimer { get; }
    public WebSuggestions WebSuggestions { get; }
    public LauncherSearch LauncherSearch { get; }
    public PinyinMatcher PinyinMatcher => PinyinMatcher.Instance;
    public IModuleCatalog Modules { get; }
    public IUpdateService UpdateService { get; }
    public IBatchRenamer BatchRenamer { get; }
    public IFolderJumpService FolderJumpService { get; }
    public FolderJumpStore FolderJumpStore { get; }
    public ISetupWizardService SetupWizardService { get; }

    private readonly CancellationTokenSource _windowWatcherCts = new();
    private Task? _windowWatcherTask;
    private string? _lastForegroundApp;
    private bool _isDisposed;

    public DesktopAppContext()
    {
        SettingsStore = new SettingsStore();
        var currentSettings = SettingsStore.Current;

        AppScanner = new LinuxAppScanner();
        FileSearcher = new LinuxFileSearcher();
        ClipboardService = new LinuxClipboardService();
        ClipboardStorage = new ClipboardStorage();
        ClipboardHistory = new ClipboardHistoryManager(
            ClipboardService,
            ClipboardStorage,
            currentSettings.Clipboard.HistoryLimit);

        AiService = new AiService(MapAiConfig(currentSettings.Ai));
        TranslationService = new TranslationService(AiService);
        AutostartService = new LinuxAutostartService();
        InputHook = new LinuxInputHook(ClipboardService);

        var resolvedLang = Pankin.Core.Localization.UiLanguageResolver.Resolve(
            currentSettings.General.UiLanguage,
            Pankin.Core.Localization.UiLanguageResolver.SystemLanguage());
        Pankin.Core.Localization.Strings.Initialize(resolvedLang);

        ActionHost = new LinuxLauncherActionHost(ClipboardService);
        PowerTimer = new PowerTimerService(ActionHost);
        WebSuggestions = new WebSuggestions();

        FolderJumpStore = new FolderJumpStore();
        FolderJumpService = new LinuxFolderJumpService(FolderJumpStore);
        BatchRenamer = new BatchRenamer();
        SetupWizardService = new SetupWizardService();

        UpdateService = new UpdateService(
            currentVersion: "0.2.0",
            automatic: () => SettingsStore.Current.Update.AutoUpdate,
            openReleaseUrlOrInstall: url => _ = ActionHost.OpenAsync(url));

        Modules = new ModuleHost(
            entries:
            [
                new DelegateCapabilityModule(new ModuleInfo(ModuleIds.Launcher, ModuleCategory.Search, "search", true, [])),
                new DelegateCapabilityModule(new ModuleInfo(ModuleIds.Clipboard, ModuleCategory.Clipboard, "clipboard", true, []),
                    onStart: _ => { ClipboardService.StartMonitoring(); return Task.CompletedTask; },
                    onStop: _ => { ClipboardService.StopMonitoring(); return Task.CompletedTask; }),
                new DelegateCapabilityModule(new ModuleInfo(ModuleIds.SuperPanel, ModuleCategory.System, "sparkles", true,
                    [new(ModuleIds.Clipboard, DependencyKind.Optional), new(ModuleIds.Ai, DependencyKind.Optional)]),
                    onStart: _ => { InputHook.SuperPanelEnabled = true; return Task.CompletedTask; },
                    onStop: _ => { InputHook.SuperPanelEnabled = false; return Task.CompletedTask; }),
                new DelegateCapabilityModule(new ModuleInfo(ModuleIds.Ai, ModuleCategory.Ai, "bot", true, [])),
                new DelegateCapabilityModule(new ModuleInfo(ModuleIds.FolderJump, ModuleCategory.Files, "folder", true, [])),
                new DelegateCapabilityModule(new ModuleInfo(ModuleIds.Rename, ModuleCategory.Files, "pencil", true, [])),
                new DelegateCapabilityModule(new ModuleInfo(ModuleIds.PowerTimer, ModuleCategory.System, "clock", true, []))
            ],
            store: SettingsStore,
            dispatch: work => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(work));

        LauncherSearch = new LauncherSearch(
            AppScanner,
            FileSearcher,
            ActionHost,
            SettingsStore,
            PowerTimer,
            WebSuggestions,
            clock: null,
            folderJumpStore: FolderJumpStore);

        SettingsStore.Changed += OnSettingsChanged;
        StartWindowWatcher();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // 1. 初始化并还原各能力模块状态
        if (Modules is ModuleHost host)
        {
            await host.InitializeAsync(ct).ConfigureAwait(false);
        }

        // 2. 后台预扫描桌面应用索引
        _ = Task.Run(() => LauncherSearch.RefreshAppsAsync(ct), ct);

        // 3. 启动 SharpHook 全局热键与划词钩子
        var s = SettingsStore.Current;
        InputHook.UpdateHotkeys(s.Launcher.Hotkey, s.Clipboard.Hotkey, s.SuperPanel.Hotkey);
        InputHook.SuperPanelEnabled = s.Modules.SuperPanel && s.SuperPanel.Enabled;
        InputHook.ConfigureSelectionFilter(s.SuperPanel);
        await InputHook.StartAsync(ct).ConfigureAwait(false);
    }

    private void OnSettingsChanged(object? sender, PankinSettings newSettings)
    {
        AiService.UpdateConfig(MapAiConfig(newSettings.Ai));

        var resolvedLang = Pankin.Core.Localization.UiLanguageResolver.Resolve(
            newSettings.General.UiLanguage,
            Pankin.Core.Localization.UiLanguageResolver.SystemLanguage());
        Pankin.Core.Localization.Strings.Initialize(resolvedLang);

        InputHook.UpdateHotkeys(newSettings.Launcher.Hotkey, newSettings.Clipboard.Hotkey, newSettings.SuperPanel.Hotkey);
        InputHook.SuperPanelEnabled = newSettings.Modules.SuperPanel && newSettings.SuperPanel.Enabled;
        InputHook.ConfigureSelectionFilter(newSettings.SuperPanel);
    }

    private static AiConfig MapAiConfig(AiConfigSettings ai)
    {
        var format = string.Equals(ai.ApiFormat, "Anthropic", StringComparison.OrdinalIgnoreCase)
            ? AiApiFormat.Anthropic
            : AiApiFormat.OpenAi;

        return new AiConfig
        {
            BaseUrl = ai.BaseUrl,
            ApiKey = ai.ApiKey,
            Model = ai.Model,
            TranslationModel = ai.TranslationModel,
            Format = format,
            DefaultTargetLanguage = ai.TargetLanguage
        };
    }

    private void StartWindowWatcher()
    {
        _windowWatcherTask = Task.Run(async () =>
        {
            var token = _windowWatcherCts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, token).ConfigureAwait(false);
                    var info = await LinuxWindowInfoResolver.GetActiveWindowInfoAsync(token).ConfigureAwait(false);
                    if (info?.AppName != null && !string.IsNullOrWhiteSpace(info.AppName))
                    {
                        if (!string.Equals(_lastForegroundApp, info.AppName, StringComparison.OrdinalIgnoreCase))
                        {
                            _lastForegroundApp = info.AppName;
                            LauncherSearch.RecordExternalAppActivation(info.AppName);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore transient errors
                }
            }
        }, _windowWatcherCts.Token);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _windowWatcherCts.Cancel();
            _windowWatcherTask?.Wait(500);
            _windowWatcherCts.Dispose();
        }
        catch
        {
            // Ignore
        }

        SettingsStore.Changed -= OnSettingsChanged;
        LauncherSearch.Dispose();
        PowerTimer.Dispose();
        WebSuggestions.Dispose();
        InputHook.Dispose();
        ClipboardHistory.Dispose();
        ClipboardService.Dispose();
        if (UpdateService is IDisposable disposableUpdate)
        {
            disposableUpdate.Dispose();
        }
        if (AiService is IDisposable disposableAi)
        {
            disposableAi.Dispose();
        }
    }
}
