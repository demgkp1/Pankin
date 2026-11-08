using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pankin.Core.Abstractions;
using Pankin.Core.Settings;
using Pankin.Core.Setup;

namespace Pankin.Desktop.ViewModels;

public partial class SetupViewModel : ViewModelBase
{
    private readonly ISetupWizardService _wizard;
    private readonly IAutostartService? _autostartService;
    private readonly SettingsStore? _settingsStore;

    public event Action? CloseRequested;

    public ObservableCollection<LinuxDependencyStatus> Dependencies { get; } = [];

    [ObservableProperty]
    private int _currentStepIndex;

    [ObservableProperty]
    private SetupStepKind _currentStep;

    [ObservableProperty]
    private string _stepTitle = "欢迎使用 Pankin";

    [ObservableProperty]
    private string _stepDescription = "Ubuntu 24.04 Linux 原生桌面全局效率中心";

    [ObservableProperty]
    private bool _canGoNext = true;

    [ObservableProperty]
    private bool _canGoPrevious;

    [ObservableProperty]
    private bool _isLastStep;

    [ObservableProperty]
    private bool _isBusy;

    // 配置字段
    [ObservableProperty]
    private string _language = "zh-CN";

    [ObservableProperty]
    private string _launcherHotkey = "Ctrl+Space";

    [ObservableProperty]
    private string _clipboardHotkey = "Alt+V";

    [ObservableProperty]
    private bool _autostart = true;

    [ObservableProperty]
    private string _aiProvider = "DeepSeek";

    [ObservableProperty]
    private string _aiApiKey = string.Empty;

    [ObservableProperty]
    private string _aiBaseUrl = "https://api.deepseek.com";

    [ObservableProperty]
    private string _aiModel = "deepseek-chat";

    public SetupViewModel(ISetupWizardService? wizard = null, IAutostartService? autostart = null, SettingsStore? settingsStore = null)
    {
        _wizard = wizard ?? new SetupWizardService();
        _autostartService = autostart;
        _settingsStore = settingsStore;
        _wizard.StepChanged += OnWizardStepChanged;
        UpdateStepState();
    }

    private void OnWizardStepChanged(object? sender, SetupStepKind step)
    {
        UpdateStepState();
    }

    private void UpdateStepState()
    {
        CurrentStep = _wizard.CurrentStep;
        CurrentStepIndex = _wizard.CurrentStepIndex;
        CanGoNext = _wizard.CanGoNext;
        CanGoPrevious = _wizard.CanGoPrevious;
        IsLastStep = _wizard.CurrentStep == SetupStepKind.Complete;

        (StepTitle, StepDescription) = CurrentStep switch
        {
            SetupStepKind.Welcome => ("欢迎使用 Pankin", "为 Ubuntu 24.04 深度打造的原生全局启动器、剪贴板与效率工具"),
            SetupStepKind.Language => ("界面语言偏好", "请选择你希望使用的界面显示语言"),
            SetupStepKind.DependencyCheck => ("系统环境与依赖就绪检测", "检查本地检索、剪贴板桥接与桌面托盘工具"),
            SetupStepKind.Hotkeys => ("全局快捷键配置", "自定义呼出启动器与剪贴板历史的核心快捷键"),
            SetupStepKind.AiProvider => ("AI 智能助手服务", "配置大语言模型 API 以启用划词翻译、解释与对话能力"),
            SetupStepKind.Autostart => ("开机自启动设置", "登录系统后自动在后台启动 Pankin"),
            SetupStepKind.Complete => ("向导完成！", "所有核心设置已就绪，欢迎开启高效 Linux 桌面体验"),
            _ => ("向导", string.Empty)
        };

        if (CurrentStep == SetupStepKind.DependencyCheck)
        {
            _ = RefreshDependenciesAsync();
        }
    }

    [RelayCommand]
    public async Task RefreshDependenciesAsync()
    {
        IsBusy = true;
        try
        {
            var list = await _wizard.CheckDependenciesAsync().ConfigureAwait(true);
            Dependencies.Clear();
            foreach (var item in list)
            {
                Dependencies.Add(item);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task NextAsync()
    {
        SyncConfigToWizard();

        if (IsLastStep)
        {
            await CompleteSetupAsync().ConfigureAwait(true);
            return;
        }

        _wizard.NextStep();
    }

    [RelayCommand]
    public void Previous()
    {
        SyncConfigToWizard();
        _wizard.PreviousStep();
    }

    private void SyncConfigToWizard()
    {
        _wizard.Configuration.Language = Language;
        _wizard.Configuration.LauncherHotkey = LauncherHotkey;
        _wizard.Configuration.ClipboardHotkey = ClipboardHotkey;
        _wizard.Configuration.Autostart = Autostart;
        _wizard.Configuration.AiProvider = AiProvider;
        _wizard.Configuration.AiApiKey = AiApiKey;
        _wizard.Configuration.AiBaseUrl = AiBaseUrl;
        _wizard.Configuration.AiModel = AiModel;
    }

    [RelayCommand]
    public async Task CompleteSetupAsync()
    {
        SyncConfigToWizard();
        IsBusy = true;
        try
        {
            // 应用配置
            var baseSettings = _settingsStore?.Current ?? new PankinSettings();
            var applied = await _wizard.ApplySetupAsync(baseSettings, _autostartService).ConfigureAwait(true);
            _settingsStore?.Save(applied);
            CloseRequested?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
