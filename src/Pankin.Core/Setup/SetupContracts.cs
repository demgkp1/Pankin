using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Pankin.Core.Abstractions;
using Pankin.Core.Settings;

namespace Pankin.Core.Setup;

public enum SetupStepKind
{
    Welcome = 0,
    Language = 1,
    DependencyCheck = 2,
    Hotkeys = 3,
    AiProvider = 4,
    Autostart = 5,
    Complete = 6
}

public sealed record LinuxDependencyStatus(
    string Name,
    string Description,
    bool IsInstalled,
    bool IsRecommended,
    string InstallCommand,
    string? DetectedPath = null);

public sealed class SetupConfiguration
{
    public string Language { get; set; } = "zh-CN";
    public string LauncherHotkey { get; set; } = "Ctrl+Space";
    public string ClipboardHotkey { get; set; } = "Alt+V";
    public bool Autostart { get; set; } = true;
    public string AiProvider { get; set; } = "DeepSeek";
    public string AiApiKey { get; set; } = string.Empty;
    public string AiBaseUrl { get; set; } = "https://api.deepseek.com";
    public string AiModel { get; set; } = "deepseek-chat";
    public string AiApiFormat { get; set; } = "OpenAi";
}

public interface IDependencyChecker
{
    Task<IReadOnlyList<LinuxDependencyStatus>> CheckDependenciesAsync(CancellationToken ct = default);
}

public interface ISetupWizardService
{
    SetupStepKind CurrentStep { get; }
    SetupConfiguration Configuration { get; }
    int TotalSteps { get; }
    int CurrentStepIndex { get; }
    bool CanGoNext { get; }
    bool CanGoPrevious { get; }

    event EventHandler<SetupStepKind>? StepChanged;

    bool NextStep();
    bool PreviousStep();
    void GoToStep(SetupStepKind step);
    Task<IReadOnlyList<LinuxDependencyStatus>> CheckDependenciesAsync(CancellationToken ct = default);
    Task<PankinSettings> ApplySetupAsync(PankinSettings settings, IAutostartService? autostart = null, CancellationToken ct = default);
}
