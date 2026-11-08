using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pankin.Core.Updates;

namespace Pankin.Desktop.ViewModels.Updates;

/// <summary>
/// 软件更新 ViewModel：负责驱动“关于”页更新状态、展示新版 Release Notes、执行手动检查更新与打开发布页
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly IUpdateService _updateService;

    public string CurrentVersion => _updateService.CurrentVersion;

    [ObservableProperty]
    private string? _latestVersion;

    [ObservableProperty]
    private UpdateState _state;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReleaseNotes))]
    private string? _releaseNotes;

    [ObservableProperty]
    private double _downloadProgress;

    public bool IsChecking => State == UpdateState.Checking;
    public bool IsAvailable => State == UpdateState.Available;
    public bool IsDownloading => State == UpdateState.Downloading;
    public bool IsReady => State == UpdateState.Ready;
    public bool IsUpToDate => State == UpdateState.UpToDate;
    public bool HasFailed => State == UpdateState.Failed;
    public bool HasReleaseNotes => !string.IsNullOrEmpty(ReleaseNotes);

    public IRelayCommand CheckUpdateCommand { get; }
    public IRelayCommand OpenReleasePageCommand { get; }
    public IRelayCommand DownloadUpdateCommand { get; }

    public UpdateViewModel(IUpdateService updateService)
    {
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));

        CheckUpdateCommand = new AsyncRelayCommand(OnCheckUpdateAsync);
        OpenReleasePageCommand = new RelayCommand(OnOpenReleasePage);
        DownloadUpdateCommand = new AsyncRelayCommand(OnDownloadUpdateAsync);

        SyncProperties();
        _updateService.Changed += OnServiceChanged;
    }

    private void SyncProperties()
    {
        State = _updateService.State;
        LatestVersion = _updateService.Latest?.Version;
        Error = _updateService.Error;
        ReleaseNotes = _updateService.Latest?.Notes;
        DownloadProgress = _updateService.DownloadProgress;

        StatusText = State switch
        {
            UpdateState.Idle => "尚未检查更新",
            UpdateState.Checking => "正在连接服务器检查新版本...",
            UpdateState.UpToDate => $"已是最新版本 ({CurrentVersion})",
            UpdateState.Available => $"发现新版本 {LatestVersion}，点击查看详情",
            UpdateState.Downloading => $"正在准备更新资源 ({DownloadProgress * 100:F0}%)",
            UpdateState.Ready => $"新版本 {LatestVersion} 已就绪，点击打开发布页面",
            UpdateState.Failed => Error ?? "检查更新失败",
            _ => string.Empty
        };

        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsUpToDate));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(HasReleaseNotes));
    }

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(SyncProperties);
    }

    private async Task OnCheckUpdateAsync()
    {
        try
        {
            await _updateService.CheckAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText = $"检查更新出错: {ex.Message}";
        }
    }

    private async Task OnDownloadUpdateAsync()
    {
        try
        {
            await _updateService.DownloadAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText = $"下载出错: {ex.Message}";
        }
    }

    private void OnOpenReleasePage()
    {
        _updateService.InstallAndRestart();
    }
}
