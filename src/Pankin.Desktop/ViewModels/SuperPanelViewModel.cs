using Pankin.Core.Selection;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pankin.Core.Ai;
using Pankin.Core.Localization;
using Pankin.Desktop.Services;

namespace Pankin.Desktop.ViewModels;

public sealed partial class ChatMessageItem : ObservableObject
{
    public AiChatRole Role { get; }
    public bool IsUser => Role == AiChatRole.User;

    [ObservableProperty]
    private string _content;

    public ChatMessageItem(AiChatRole role, string content)
    {
        Role = role;
        _content = content;
    }
}

public partial class SuperPanelViewModel : ViewModelBase
{
    private readonly DesktopAppContext _context;
    private CancellationTokenSource? _translateCts;
    private CancellationTokenSource? _chatCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeTitle))]
    private bool _isChatMode; // false = 翻译模式, true = 对话模式

    public string ModeTitle => IsChatMode ? Strings.Get("SuperPanel_ModeChat") : Strings.Get("SuperPanel_ModeTranslate");

    [ObservableProperty]
    private string _sourceText = string.Empty;

    [ObservableProperty]
    private string? _sourceAppName;

    [ObservableProperty]
    private string? _windowTitle;

    [ObservableProperty]
    private bool _isUrl;

    [ObservableProperty]
    private bool _isFilePath;

    [ObservableProperty]
    private bool _canBatchRename;

    [ObservableProperty]
    private SelectionContext? _currentContext;

    [ObservableProperty]
    private string _targetLanguage = "auto";

    [ObservableProperty]
    private string _translationResult = string.Empty;

    [ObservableProperty]
    private bool _isTranslating;

    [ObservableProperty]
    private string _currentModelName = string.Empty;

    [ObservableProperty]
    private string _chatInput = string.Empty;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isSettingsExpanded;

    [ObservableProperty]
    private string _configBaseUrl = string.Empty;

    [ObservableProperty]
    private string _configApiKey = string.Empty;

    [ObservableProperty]
    private string _configModel = string.Empty;

    [ObservableProperty]
    private string _configApiFormat = "OpenAi";

    [ObservableProperty]
    private AiPreset? _selectedPreset;

    [ObservableProperty]
    private string _settingsStatusMessage = string.Empty;

    public ObservableCollection<AiPreset> Presets { get; } = [];
    public ObservableCollection<ChatMessageItem> Messages { get; } = [];

    public event Action? RequestHideWindow;
    public event Action<IReadOnlyList<string>>? RequestOpenRename;

    public SuperPanelViewModel() : this(DesktopAppContext.Instance)
    {
    }

    public SuperPanelViewModel(DesktopAppContext context)
    {
        _context = context;

        foreach (var p in AiPresets.All)
        {
            Presets.Add(p);
        }

        LoadCurrentSettings();
        _context.SettingsStore.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(LoadCurrentSettings);
    }

    public void LoadCurrentSettings()
    {
        var ai = _context.SettingsStore.Current.Ai;
        ConfigBaseUrl = ai.BaseUrl;
        ConfigApiKey = ai.ApiKey ?? string.Empty;
        ConfigModel = ai.Model;
        ConfigApiFormat = ai.ApiFormat;
        CurrentModelName = ai.Model;

        SelectedPreset = Presets.FirstOrDefault(p =>
            string.Equals(p.BaseUrl.TrimEnd('/'), ai.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
    }

    public void SetSelection(SelectionContext context, string mode = "translate")
    {
        CurrentContext = context;
        SourceAppName = context.SourceAppName;
        WindowTitle = context.WindowTitle;
        IsUrl = context.IsUrl;
        IsFilePath = context.IsFilePath;
        CanBatchRename = (context.HasFiles && context.Files is { Count: > 0 }) || context.IsFilePath;

        if (context.HasFiles && context.Files is { Count: > 0 } files)
        {
            SourceText = string.Join("\n", files);
            IsChatMode = false;
            TranslationResult = Strings.Get("SuperPanel_FilesSelected", files.Count) + "\n" + string.Join("\n", files.Take(5));
            return;
        }

        if (!string.IsNullOrWhiteSpace(context.Text))
        {
            SetSelectedText(context.Text, mode);
        }
    }

    public void SetSelectedText(string text, string mode = "translate")
    {
        IsUrl = SelectionContext.DetectIsUrl(text);
        IsFilePath = SelectionContext.DetectIsFilePath(text);
        CanBatchRename = IsFilePath;
        if (string.IsNullOrWhiteSpace(text)) return;

        SourceText = text.Trim();

        var settings = _context.SettingsStore.Current;
        if (!settings.Modules.Ai || !settings.Ai.Enabled || mode == "action")
        {
            IsChatMode = false;
            IsSettingsExpanded = false;
            TranslationResult = "已捕获选中文本，可直接点击下方快捷动作执行操作。";
            return;
        }

        if (mode == "chat")
        {
            IsChatMode = true;
            ChatInput = SourceText;
            _ = SendChatMessageAsync();
        }
        else if (mode == "summary")
        {
            IsChatMode = true;
            ChatInput = $"{Strings.Get("SuperPanel_Action_Summary")}：\n\n{SourceText}";
            _ = SendChatMessageAsync();
        }
        else
        {
            IsChatMode = false;
            _ = TranslateAsync();
        }
    }

    partial void OnSelectedPresetChanged(AiPreset? value)
    {
        if (value == null) return;
        ConfigBaseUrl = value.BaseUrl;
        ConfigModel = value.Model;
        ConfigApiFormat = value.Format == AiApiFormat.Anthropic ? "Anthropic" : "OpenAi";
        CurrentModelName = value.Model;
    }

    [RelayCommand]
    public async Task TranslateAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceText)) return;

        IsChatMode = false;
        _translateCts?.Cancel();
        _translateCts?.Dispose();
        _translateCts = new CancellationTokenSource();
        var ct = _translateCts.Token;

        IsTranslating = true;
        TranslationResult = Strings.Get("SuperPanel_Translating");

        var sb = new StringBuilder();
        try
        {
            await foreach (var chunk in _context.TranslationService.StreamTranslateAsync(SourceText, TargetLanguage, null, null, ct).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested) break;
                sb.Append(chunk);
                var current = sb.ToString();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    TranslationResult = current;
                });
            }

            if (sb.Length == 0 && !ct.IsCancellationRequested)
            {
                TranslationResult = Strings.Get("SuperPanel_NoResult");
            }
        }
        catch (AiCredentialException)
        {
            TranslationResult = Strings.Get("SuperPanel_NoApiKey");
            IsSettingsExpanded = false;
        }
        catch (Exception ex)
        {
            TranslationResult = Strings.Get("SuperPanel_TranslateError", ex.Message);
        }
        finally
        {
            IsTranslating = false;
        }
    }

    [RelayCommand]
    public async Task SendChatMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(ChatInput) || IsStreaming) return;

        IsChatMode = true;
        var userText = ChatInput.Trim();
        ChatInput = string.Empty;

        Messages.Add(new ChatMessageItem(AiChatRole.User, userText));

        var assistantMsg = new ChatMessageItem(AiChatRole.Assistant, "正在思考...");
        Messages.Add(assistantMsg);

        _chatCts?.Cancel();
        _chatCts?.Dispose();
        _chatCts = new CancellationTokenSource();
        var ct = _chatCts.Token;

        IsStreaming = true;

        var chatHistory = new List<AiChatMessage>();
        foreach (var m in Messages)
        {
            if (m == assistantMsg) continue;
            chatHistory.Add(new AiChatMessage(m.Role, m.Content));
        }

        var sb = new StringBuilder();
        try
        {
            await foreach (var chunk in _context.AiService.StreamChatAsync(chatHistory, null, ct).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested) break;
                sb.Append(chunk);
                var current = sb.ToString();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    assistantMsg.Content = current;
                });
            }

            if (sb.Length == 0 && !ct.IsCancellationRequested)
            {
                assistantMsg.Content = "（未收到回复内容）";
            }
        }
        catch (AiCredentialException)
        {
            assistantMsg.Content = "⚠️ 未配置有效 API Key，请点击右上角⚙️配置。";
            IsSettingsExpanded = false;
        }
        catch (Exception ex)
        {
            assistantMsg.Content = $"请求失败: {ex.Message}";
        }
        finally
        {
            IsStreaming = false;
        }
    }

    [RelayCommand]
    public void StopChat()
    {
        _chatCts?.Cancel();
        IsStreaming = false;
    }

    [RelayCommand]
    public void SwitchToTranslate()
    {
        IsChatMode = false;
        if (!string.IsNullOrEmpty(SourceText))
        {
            _ = TranslateAsync();
        }
    }

    [RelayCommand]
    public void SwitchToChat()
    {
        IsChatMode = true;
        if (!string.IsNullOrEmpty(SourceText) && Messages.Count == 0)
        {
            ChatInput = SourceText;
        }
    }

    [RelayCommand]
    public void SummarizeText()
    {
        if (string.IsNullOrWhiteSpace(SourceText)) return;
        IsChatMode = true;
        ChatInput = $"请简要总结下面的内容，先给结论再列不超过3个要点：\n\n{SourceText}";
        _ = SendChatMessageAsync();
    }

    [RelayCommand]
    public async Task CopySourceTextAsync()
    {
        if (!string.IsNullOrEmpty(SourceText))
        {
            await _context.ClipboardService.SetTextAsync(SourceText);
            RequestHideWindow?.Invoke();
        }
    }

    [RelayCommand]
    public async Task CopyTranslationResultAsync()
    {
        if (!string.IsNullOrEmpty(TranslationResult))
        {
            await _context.ClipboardService.SetTextAsync(TranslationResult);
            RequestHideWindow?.Invoke();
        }
    }

    [RelayCommand]
    public void SearchWeb()
    {
        var query = !string.IsNullOrWhiteSpace(SourceText) ? SourceText : TranslationResult;
        if (!string.IsNullOrWhiteSpace(query))
        {
            RequestHideWindow?.Invoke();
            try
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        ArgumentList = { $"https://cn.bing.com/search?q={Uri.EscapeDataString(query)}" },
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
            }
            catch { }
        }
    }

    [RelayCommand]
    public void ExplainText()
    {
        if (string.IsNullOrWhiteSpace(SourceText)) return;
        IsChatMode = true;
        ChatInput = $"请深入浅出地解释以下内容，包括核心概念和关键背景：\n\n{SourceText}";
        _ = SendChatMessageAsync();
    }

    [RelayCommand]
    public void OpenUrl()
    {
        if (string.IsNullOrWhiteSpace(SourceText)) return;
        RequestHideWindow?.Invoke();
        try
        {
            var url = SourceText.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    ArgumentList = { url },
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
        }
        catch { }
    }

    [RelayCommand]
    public void OpenFileOrFolder()
    {
        if (string.IsNullOrWhiteSpace(SourceText)) return;
        RequestHideWindow?.Invoke();
        try
        {
            var path = SourceText.Trim();
            if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                path = new Uri(path).LocalPath;
            }
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    ArgumentList = { path },
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
        }
        catch { }
    }

    [RelayCommand]
    public void BatchRename()
    {
        var files = new List<string>();
        if (CurrentContext?.HasFiles == true && CurrentContext.Files != null)
        {
            files.AddRange(CurrentContext.Files);
        }
        else if (!string.IsNullOrWhiteSpace(SourceText))
        {
            var lines = SourceText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = new Uri(trimmed).LocalPath;
                }
                if (System.IO.File.Exists(trimmed) || System.IO.Directory.Exists(trimmed))
                {
                    files.Add(trimmed);
                }
            }
            if (files.Count == 0 && (System.IO.File.Exists(SourceText) || System.IO.Directory.Exists(SourceText)))
            {
                files.Add(SourceText);
            }
        }

        RequestHideWindow?.Invoke();
        RequestOpenRename?.Invoke(files);
    }

    [RelayCommand]
    public async Task ReplaceSelectionAsync()
    {
        var textToPaste = !string.IsNullOrEmpty(TranslationResult) ? TranslationResult : SourceText;
        if (string.IsNullOrEmpty(textToPaste)) return;
        RequestHideWindow?.Invoke();
        await _context.ClipboardService.SetTextAsync(textToPaste);
    }

    [RelayCommand]
    public void SaveAiSettings()
    {
        try
        {
            _context.SettingsStore.Update(s => s with
            {
                Ai = s.Ai with
                {
                    BaseUrl = ConfigBaseUrl.Trim(),
                    ApiKey = string.IsNullOrWhiteSpace(ConfigApiKey) ? null : ConfigApiKey.Trim(),
                    Model = ConfigModel.Trim(),
                    ApiFormat = ConfigApiFormat
                }
            });

            CurrentModelName = ConfigModel.Trim();
            SettingsStatusMessage = "✅ 配置已保存并即时生效";
            _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.UIThread.Post(() => SettingsStatusMessage = string.Empty));
        }
        catch (Exception ex)
        {
            SettingsStatusMessage = $"❌ 保存失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ToggleSettings()
    {
        IsSettingsExpanded = !IsSettingsExpanded;
    }
}
