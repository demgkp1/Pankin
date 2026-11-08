using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pankin.Core.Modules;
using Pankin.Desktop.Services;

namespace Pankin.Desktop.ViewModels.Modules;

public sealed partial class ModuleItemViewModel : ObservableObject
{
    private readonly IModuleCatalog _catalog;
    private readonly Action<ModuleChange> _onApplyChange;

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string Glyph { get; }
    public ModuleCategory Category { get; }
    public StreamGeometry IconGeometry => LucideIcons.GetGeometry(GetIconPath(Id));

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private ModuleStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    public IRelayCommand ToggleCommand { get; }

    public ModuleItemViewModel(ModuleInfo info, ModuleState state, IModuleCatalog catalog, Action<ModuleChange> onApplyChange)
    {
        _catalog = catalog;
        _onApplyChange = onApplyChange;

        Id = info.Id;
        Glyph = info.Glyph;
        Category = info.Category;
        Name = GetDisplayName(info.Id);
        Description = GetDescription(info.Id);

        _isEnabled = state.Enabled;
        _status = state.Status;
        _problem = state.Problem;

        ToggleCommand = new RelayCommand(OnToggle);
    }

    private void OnToggle()
    {
        bool next = !IsEnabled;
        var change = _catalog.Preview(Id, next);
        _onApplyChange(change);
    }

    public void UpdateState(ModuleState state)
    {
        IsEnabled = state.Enabled;
        Status = state.Status;
        Problem = state.Problem;
    }

    private static string GetDisplayName(string id) => id switch
    {
        ModuleIds.Launcher => "应用与文件启动器",
        ModuleIds.Clipboard => "剪贴板历史管理器",
        ModuleIds.Gestures => "鼠标全局手势",
        ModuleIds.SuperPanel => "划词超级面板",
        ModuleIds.Ai => "AI 智能问答与翻译",
        ModuleIds.FolderJump => "文件夹穿梭跳转",
        ModuleIds.Rename => "批量重命名",
        ModuleIds.PowerTimer => "定时关机与电源管理",
        _ => id
    };

    private static string GetIconPath(string id) => id switch
    {
        ModuleIds.Launcher => LucideIcons.Search,
        ModuleIds.Clipboard => LucideIcons.Clipboard,
        ModuleIds.Gestures => LucideIcons.Mouse,
        ModuleIds.SuperPanel => LucideIcons.Wand,
        ModuleIds.Ai => LucideIcons.Ai,
        ModuleIds.FolderJump => LucideIcons.Folder,
        ModuleIds.Rename => LucideIcons.Pencil,
        ModuleIds.PowerTimer => LucideIcons.Timer,
        _ => LucideIcons.Grid
    };

    private static string GetDescription(string id) => id switch
    {
        ModuleIds.Launcher => "快速搜索应用、文件、计算器与终端命令，支持首字母拼音快速定位",
        ModuleIds.Clipboard => "记录剪贴板历史内容，支持纯文本、图像与文件片段快捷粘贴与置顶",
        ModuleIds.Gestures => "按住鼠标右键划动即可触发后退、关闭标签页或唤起应用面板",
        ModuleIds.SuperPanel => "选中文本或按下快捷键就近弹出快捷操作面板，支持 AI 翻译与搜索",
        ModuleIds.Ai => "接入大语言模型提供多轮流式会话与高保真段落中英互译",
        ModuleIds.FolderJump => "文件对话框快速跳转到常用与当前激活的文件夹目录",
        ModuleIds.Rename => "支持多规则叠加与秒级实时预览的安全批量重命名工具",
        ModuleIds.PowerTimer => "自然语言倒计时与指定时间自动关机、重启或休眠",
        _ => "扩展能力模块"
    };
}

public sealed partial class ModulesSettingsViewModel : ObservableObject
{
    private readonly IModuleCatalog _catalog;

    public ObservableCollection<ModuleItemViewModel> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingConfirm))]
    private ModuleChange? _pendingConfirmChange;

    public bool HasPendingConfirm => PendingConfirmChange != null;

    [ObservableProperty]
    private string? _confirmMessage;

    public IRelayCommand ConfirmChangeCommand { get; }
    public IRelayCommand CancelConfirmCommand { get; }

    public ModulesSettingsViewModel(IModuleCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

        ConfirmChangeCommand = new AsyncRelayCommand(OnConfirmChangeAsync);
        CancelConfirmCommand = new RelayCommand(() => PendingConfirmChange = null);

        RefreshItems();
        _catalog.Changed += OnCatalogChanged;
    }

    private void RefreshItems()
    {
        Items.Clear();
        foreach (var info in _catalog.Modules)
        {
            var state = _catalog.StateOf(info.Id);
            Items.Add(new ModuleItemViewModel(info, state, _catalog, RequestChange));
        }
    }

    private void OnCatalogChanged(object? sender, string id)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = Items.FirstOrDefault(i => i.Id == id);
            if (item != null)
            {
                item.UpdateState(_catalog.StateOf(id));
            }
        });
    }

    private void RequestChange(ModuleChange change)
    {
        if (change.AlsoEnable.Count > 0 || change.AlsoDisable.Count > 0 || change.RestoresSystemSetting)
        {
            PendingConfirmChange = change;
            ConfirmMessage = change.Enable
                ? $"启用此模块将连带启用依赖模块: {string.Join(", ", change.AlsoEnable)}"
                : $"停用此模块将连带停用依赖它的模块: {string.Join(", ", change.AlsoDisable)}";
            return;
        }

        _ = ApplyChangeAsync(change);
    }

    private async Task OnConfirmChangeAsync()
    {
        if (PendingConfirmChange is { } change)
        {
            PendingConfirmChange = null;
            await ApplyChangeAsync(change);
        }
    }

    private async Task ApplyChangeAsync(ModuleChange change)
    {
        try
        {
            await _catalog.ApplyAsync(change, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // 失败信息已由 ModuleHost 记入 ModuleState.Problem
            System.Diagnostics.Debug.WriteLine($"应用模块变更失败: {ex.Message}");
        }
    }
}
