using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Pankin.Core.Abstractions;
using Pankin.Core.Settings;

namespace Pankin.Core.Setup;

public sealed class SetupWizardService : ISetupWizardService
{
    private static readonly SetupStepKind[] Steps =
    [
        SetupStepKind.Welcome,
        SetupStepKind.Language,
        SetupStepKind.DependencyCheck,
        SetupStepKind.Hotkeys,
        SetupStepKind.AiProvider,
        SetupStepKind.Autostart,
        SetupStepKind.Complete
    ];

    private int _currentIndex;
    private readonly IDependencyChecker _dependencyChecker;

    public SetupConfiguration Configuration { get; } = new();
    public SetupStepKind CurrentStep => Steps[_currentIndex];
    public int TotalSteps => Steps.Length;
    public int CurrentStepIndex => _currentIndex;
    public bool CanGoNext => _currentIndex < Steps.Length - 1;
    public bool CanGoPrevious => _currentIndex > 0;

    public event EventHandler<SetupStepKind>? StepChanged;

    public SetupWizardService(IDependencyChecker? dependencyChecker = null)
    {
        _dependencyChecker = dependencyChecker ?? new LinuxDependencyChecker();
    }

    public bool NextStep()
    {
        if (!CanGoNext) return false;
        _currentIndex++;
        StepChanged?.Invoke(this, CurrentStep);
        return true;
    }

    public bool PreviousStep()
    {
        if (!CanGoPrevious) return false;
        _currentIndex--;
        StepChanged?.Invoke(this, CurrentStep);
        return true;
    }

    public void GoToStep(SetupStepKind step)
    {
        var idx = Array.IndexOf(Steps, step);
        if (idx >= 0 && idx != _currentIndex)
        {
            _currentIndex = idx;
            StepChanged?.Invoke(this, CurrentStep);
        }
    }

    public Task<IReadOnlyList<LinuxDependencyStatus>> CheckDependenciesAsync(CancellationToken ct = default)
    {
        return _dependencyChecker.CheckDependenciesAsync(ct);
    }

    public Task<PankinSettings> ApplySetupAsync(PankinSettings settings, IAutostartService? autostart = null, CancellationToken ct = default)
    {
        var newGeneral = settings.General with
        {
            UiLanguage = Configuration.Language,
            StartAtLogin = Configuration.Autostart,
            SetupCompleted = true
        };

        var newLauncher = settings.Launcher with
        {
            Hotkey = Configuration.LauncherHotkey
        };

        var newClipboard = settings.Clipboard with
        {
            Hotkey = Configuration.ClipboardHotkey
        };

        var newAi = settings.Ai with
        {
            ApiKey = Configuration.AiApiKey,
            BaseUrl = Configuration.AiBaseUrl,
            Model = Configuration.AiModel,
            ApiFormat = Configuration.AiApiFormat
        };

        var updated = settings with
        {
            General = newGeneral,
            Launcher = newLauncher,
            Clipboard = newClipboard,
            Ai = newAi
        };

        if (autostart != null)
        {
            try
            {
                autostart.SetAutostart(Configuration.Autostart);
            }
            catch
            {
                // 自启动设置异常不破坏配置更新
            }
        }

        return Task.FromResult(updated);
    }
}
