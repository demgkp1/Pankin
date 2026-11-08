using System.ComponentModel;
using System.Globalization;

namespace Pankin.Core.Localization;

/// <summary>
/// Pankin 全局本地化字符串资源中心，支持多语言、多级回退、伪本地化测试与动态数据绑定。
/// </summary>
public sealed class Strings : INotifyPropertyChanged
{
    private static readonly Lazy<Strings> LazyInstance = new(() => new Strings());
    public static Strings Current => LazyInstance.Value;
    public static Strings Instance => LazyInstance.Value;

    public event PropertyChangedEventHandler? PropertyChanged;

    private string _currentLanguage = "zh-CN";
    private CultureInfo _culture = CultureInfo.GetCultureInfo("zh-CN");
    private bool _isPseudoEnabled;

    public string CurrentLanguage => _currentLanguage;
    public CultureInfo Culture => _culture;

    public string this[string key] => Get(key);

    public Strings()
    {
        _isPseudoEnabled = IsPseudoLocalizationEnabled();
    }

    public static void Initialize(string language)
    {
        Current.SetLanguage(language);
    }

    public void SetLanguage(string language)
    {
        var normalized = UiLanguageResolver.Resolve(language, string.Empty);
        _currentLanguage = normalized;
        _isPseudoEnabled = IsPseudoLocalizationEnabled();

        try
        {
            _culture = CultureInfo.GetCultureInfo(normalized switch
            {
                "zh-Hans" => "zh-CN",
                "zh-Hant" => "zh-TW",
                _ => normalized
            });
        }
        catch
        {
            _culture = CultureInfo.InvariantCulture;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public static string Get(string key, params object[] args)
    {
        return Current.GetString(key, args);
    }

    public string GetString(string key, params object[] args)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;

        string? raw = null;

        // 1. 查找当前语言
        if (TryGetFromCatalog(_currentLanguage, key, out var val))
        {
            raw = val;
        }
        // 2. 回退链：若为繁体则回退至简体
        else if ((_currentLanguage == "zh-Hant" || _currentLanguage == "zh-TW") && TryGetFromCatalog("zh-CN", key, out var fallbackZh))
        {
            raw = fallbackZh;
        }
        // 3. 回退链：英文未找到则回退至中文
        else if (TryGetFromCatalog("zh-CN", key, out var defaultVal))
        {
            raw = defaultVal;
        }
        else
        {
            raw = key;
        }

        if (_isPseudoEnabled)
        {
            raw = PseudoLocalize(raw);
        }

        if (args is { Length: > 0 })
        {
            try
            {
                return string.Format(_culture, raw, args);
            }
            catch
            {
                return raw;
            }
        }

        return raw;
    }

    public static string PseudoLocalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return "［" + text + new string('～', (int)Math.Ceiling(text.Length * 0.4)) + "］";
    }

    private static bool IsPseudoLocalizationEnabled()
    {
        return Environment.GetEnvironmentVariable("PANKIN_PSEUDO_LOC") == "1"
            || Environment.GetEnvironmentVariable("PANE_PSEUDO_LOC") == "1";
    }

    private static bool TryGetFromCatalog(string language, string key, out string value)
    {
        var norm = language.ToLowerInvariant();
        if (norm is "zh-cn" or "zh-hans" or "zh")
        {
            return ZhCnCatalog.TryGetValue(key, out value!);
        }

        if (norm is "en-us" or "en")
        {
            return EnUsCatalog.TryGetValue(key, out value!);
        }

        if (norm is "zh-hant" or "zh-tw" or "zh-hk")
        {
            return ZhHantCatalog.TryGetValue(key, out value!);
        }

        value = null!;
        return false;
    }

    #region String Catalogs

    private static readonly Dictionary<string, string> ZhCnCatalog = new(StringComparer.Ordinal)
    {
        // Common
        ["Common_Settings"] = "设置",
        ["Common_Save"] = "保存",
        ["Common_Cancel"] = "取消",
        ["Common_Close"] = "关闭",
        ["Common_Copy"] = "复制",
        ["Common_Search"] = "搜索",
        ["Common_Clear"] = "清空",
        ["Common_Retry"] = "重试",
        ["Common_GoToSettings"] = "去设置",
        ["Common_Remove"] = "移除",
        ["Common_Add"] = "添加",
        ["Common_Translate"] = "翻译",
        ["Common_AskAi"] = "问 AI",
        ["Common_Summary"] = "总结",
        ["Common_Explain"] = "解释",
        ["Common_OpenLink"] = "打开链接",
        ["Common_OpenPath"] = "打开路径",
        ["Common_Copied"] = "已复制",
        ["Common_Success"] = "成功",
        ["Common_Failed"] = "失败",

        // Launcher
        ["Launcher_Title"] = "Pankin 启动器",
        ["Launcher_SearchPlaceholder"] = "搜索应用、文件，或输入任何文字 (支持数学算式)",
        ["Launcher_Group_Calculation"] = "算式计算",
        ["Launcher_Group_Timer"] = "定时任务",
        ["Launcher_Group_Applications"] = "应用程序",
        ["Launcher_Group_Recent"] = "最近使用",
        ["Launcher_Group_Pinned"] = "已固定",
        ["Launcher_Group_Commands"] = "系统指令",
        ["Launcher_Group_Terminal"] = "终端命令",
        ["Launcher_Group_Files"] = "文件与目录",
        ["Launcher_Group_Web"] = "网页搜索",
        ["Launcher_Group_Ai"] = "AI 助手",
        ["Launcher_Group_Settings"] = "系统设置",
        ["Launcher_TranslateQuery"] = "翻译“{0}”",
        ["Launcher_TranslateDescription"] = "用 AI 翻译",
        ["Launcher_AskQuery"] = "向 AI 提问“{0}”",
        ["Launcher_AskDescription"] = "发送单轮提问",
        ["Launcher_RecentApps"] = "最近使用",
        ["Launcher_PinnedApps"] = "已固定应用",
        ["Launcher_QuickActions"] = "常用操作",
        ["Launcher_QuickDirect"] = "快捷直达",

        // SuperPanel
        ["SuperPanel_Title"] = "Pankin 超级面板",
        ["SuperPanel_ModeTranslate"] = "自动翻译",
        ["SuperPanel_ModeChat"] = "AI 对话",
        ["SuperPanel_Translating"] = "正在流式翻译...",
        ["SuperPanel_TranslateError"] = "翻译失败: {0}",
        ["SuperPanel_NoResult"] = "（未收到翻译结果，请检查网络或 API Key）",
        ["SuperPanel_NoApiKey"] = "⚠️ 未配置有效 API Key，请点击右上角⚙️图标配置服务商。",
        ["SuperPanel_CopyTranslation"] = "复制译文",
        ["SuperPanel_CopySource"] = "复制原文",
        ["SuperPanel_SourceApp"] = "来源: {0}",
        ["SuperPanel_FilesSelected"] = "已选中 {0} 个文件:",
        ["SuperPanel_ChatPlaceholder"] = "向 AI 发送消息 (Enter 发送)...",
        ["SuperPanel_Send"] = "发送",
        ["SuperPanel_ClearChat"] = "清空对话",
        ["SuperPanel_QuickSettings"] = "快速配置 AI",
        ["SuperPanel_ApiBase"] = "地址:",
        ["SuperPanel_ApiKey"] = "Key:",
        ["SuperPanel_Model"] = "模型:",
        ["SuperPanel_Action_Copy"] = "复制",
        ["SuperPanel_Action_Translate"] = "翻译",
        ["SuperPanel_Action_Chat"] = "问 AI",
        ["SuperPanel_Action_Summary"] = "总结",
        ["SuperPanel_Action_Explain"] = "解释",
        ["SuperPanel_Action_Search"] = "搜索",
        ["SuperPanel_Action_OpenLink"] = "打开链接",
        ["SuperPanel_Action_OpenPath"] = "打开路径",

        // Clipboard
        ["Clipboard_Title"] = "Pankin 剪贴板历史",
        ["Clipboard_SearchPlaceholder"] = "搜索剪贴板",
        ["Clipboard_ClearAll"] = "清空所有未固定的历史记录",
        ["Clipboard_Empty"] = "暂无剪贴板历史记录",
        ["Clipboard_CopyTip"] = "按 Enter 粘贴所选项，Del 删除",
        ["Clipboard_Pinned"] = "已固定",
        ["Clipboard_Unpin"] = "取消固定",
        ["Clipboard_Pin"] = "固定此项",
        ["Clipboard_Delete"] = "删除",

        // Settings Navigation
        ["Settings_Title"] = "Pankin 设置中心",
        ["Settings_NavGeneral"] = "通用",
        ["Settings_NavModules"] = "能力",
        ["Settings_NavLauncher"] = "启动器",
        ["Settings_NavClipboard"] = "剪贴板",
        ["Settings_NavGestures"] = "鼠标手势",
        ["Settings_NavSuperPanel"] = "超级面板",
        ["Settings_NavFolderJump"] = "文件夹跳转",
        ["Settings_NavBatchRename"] = "批量重命名",
        ["Settings_NavPowerTimer"] = "定时关机",
        ["Settings_NavAi"] = "AI 服务",
        ["Settings_NavAbout"] = "关于",

        // Settings General
        ["Settings_Startup"] = "系统启动",
        ["Settings_StartAtLogin"] = "开机自启动",
        ["Settings_StartAtLogin_Description"] = "登录系统后在后台自动静默启动 Pankin",
        ["Settings_ShowTrayIcon"] = "显示托盘图标",
        ["Settings_ShowTrayIcon_Description"] = "在系统状态栏显示托盘图标，随时呼出或退出",
        ["Settings_Appearance"] = "外观与界面",
        ["Settings_Theme"] = "界面主题",
        ["Settings_Theme_System"] = "跟随系统",
        ["Settings_Theme_Light"] = "浅色",
        ["Settings_Theme_Dark"] = "深色",
        ["Settings_Language"] = "界面语言",
        ["Settings_Language_System"] = "跟随系统",
        ["Settings_Language_ZhCn"] = "简体中文",
        ["Settings_Language_EnUs"] = "English",
        ["Settings_AccentColor"] = "主题强调色",
        ["Settings_PanelRadius"] = "窗口圆角",
        ["Settings_PanelRadius_Small"] = "紧凑小圆角 (8px)",
        ["Settings_PanelRadius_Large"] = "典雅大圆角 (20px)",

        // Settings Launcher
        ["Settings_Launcher_Hotkey"] = "呼出快捷键",
        ["Settings_Launcher_DoubleTap"] = "双击 Ctrl 快速呼出",
        ["Settings_Launcher_DoubleTap_Description"] = "快速轻按两次 Control 键呼出启动器",
        ["Settings_Launcher_SearchEngine"] = "默认网页搜索引擎",

        // Settings Clipboard
        ["Settings_Clipboard_Hotkey"] = "剪贴板唤出快捷键",
        ["Settings_Clipboard_MaxItems"] = "历史记录最大保存数量",
        ["Settings_Clipboard_RecordText"] = "记录文本剪贴",
        ["Settings_Clipboard_RecordImages"] = "记录图片剪贴",

        // Settings SuperPanel
        ["Settings_Super_Trigger"] = "呼出按键",
        ["Settings_Super_HoldDuration"] = "长按触发时长",
        ["Settings_Super_AutoTranslate"] = "划词自动翻译",
        ["Settings_Super_AutoTranslate_Description"] = "选中外文文字弹出超级面板时立即自动翻译",
        ["Settings_Super_MinLength"] = "最小文字长度",
        ["Settings_Super_MaxLength"] = "最大文字长度",
        ["Settings_Super_PauseFullscreen"] = "全屏时暂停响应",
        ["Settings_Super_Blacklist"] = "应用黑名单",
        ["Settings_Super_Blacklist_Placeholder"] = "输入应用名称或进程名...",

        // Settings AI
        ["Settings_Ai_Provider"] = "服务提供商",
        ["Settings_Ai_BaseUrl"] = "API 基础地址",
        ["Settings_Ai_ApiKey"] = "API 密钥 (Key)",
        ["Settings_Ai_Model"] = "主语言模型",
        ["Settings_Ai_TranslationModel"] = "专用翻译模型",
        ["Settings_Ai_Format"] = "协议格式",
        ["Settings_Ai_Save"] = "保存配置",
        ["Settings_Ai_Test"] = "测试连接",

        // Translate
        ["Translate_Title"] = "翻译",
        ["Translate_AutoDetect"] = "自动识别",
        ["Translate_InputPlaceholder"] = "输入或粘贴要翻译的文字",
        ["Translate_Translating"] = "正在翻译...",
        ["Translate_LanguageChinese"] = "中文",
        ["Translate_LanguageEnglish"] = "英文",
        ["Translate_Copied"] = "已复制译文",
        ["Translate_Failed"] = "翻译失败",

        // Tray
        ["Tray_Settings"] = "设置",
        ["Tray_UpdateReady"] = "Pankin {0} 已准备好更新",
        ["Tray_CheckForUpdates"] = "检查更新",
        ["Launcher_Group_Favorites"] = "常用",

        // Modules & Updates
        ["Module_DependencyNotRunning"] = "模块 '{0}' 的前置硬依赖未在运行",
        ["Module_RestartPending"] = "此改动需要重启 Pankin 后生效",
        ["Update_CheckTimeout"] = "检查更新超时",
        ["Update_DownloadTimeout"] = "下载更新超时",
        ["Update_RateLimited"] = "请求过于频繁，已被 GitHub 限制，请稍后重试",
        ["Update_NetworkFailed"] = "网络连接失败，请检查网络设置",
        ["Update_StorageFailed"] = "更新缓存读写失败",
        ["Update_InvalidResponse"] = "服务器响应格式无效",
        ["Update_ReadyNotification"] = "Pankin {0} 已准备好更新",

        // Rename & FolderJump & Setup
        ["Rename_Title"] = "批量重命名",
        ["Rename_NotFolder"] = "指定路径不是文件夹",
        ["Rename_LinkedFolder"] = "不支持链接文件夹",
        ["Rename_Cancelled"] = "操作已被取消",
        ["Rename_PreviewInvalid"] = "预览状态不合法或存在冲突",
        ["Rename_RelatedRollback"] = "关联文件失败，事务已回滚",
        ["FolderJump_Title"] = "文件夹跳转",
        ["Launcher_Group_FolderJump"] = "文件夹跳转",
        ["Setup_Title"] = "首次运行配置向导",
        ["SuperPanel_Action_BatchRename"] = "批量重命名"
    };

    private static readonly Dictionary<string, string> EnUsCatalog = new(StringComparer.Ordinal)
    {
        // Common
        ["Common_Settings"] = "Settings",
        ["Common_Save"] = "Save",
        ["Common_Cancel"] = "Cancel",
        ["Common_Close"] = "Close",
        ["Common_Copy"] = "Copy",
        ["Common_Search"] = "Search",
        ["Common_Clear"] = "Clear",
        ["Common_Retry"] = "Retry",
        ["Common_GoToSettings"] = "Settings",
        ["Common_Remove"] = "Remove",
        ["Common_Add"] = "Add",
        ["Common_Translate"] = "Translate",
        ["Common_AskAi"] = "Ask AI",
        ["Common_Summary"] = "Summary",
        ["Common_Explain"] = "Explain",
        ["Common_OpenLink"] = "Open Link",
        ["Common_OpenPath"] = "Open Path",
        ["Common_Copied"] = "Copied",
        ["Common_Success"] = "Success",
        ["Common_Failed"] = "Failed",

        // Launcher
        ["Launcher_Title"] = "Pankin Launcher",
        ["Launcher_SearchPlaceholder"] = "Search apps, files, or enter any text (supports math expressions)",
        ["Launcher_Group_Calculation"] = "Calculation",
        ["Launcher_Group_Timer"] = "Timer",
        ["Launcher_Group_Applications"] = "Applications",
        ["Launcher_Group_Recent"] = "Recent",
        ["Launcher_Group_Pinned"] = "Pinned",
        ["Launcher_Group_Commands"] = "Commands",
        ["Launcher_Group_Terminal"] = "Terminal",
        ["Launcher_Group_Files"] = "Files & Folders",
        ["Launcher_Group_Web"] = "Web Search",
        ["Launcher_Group_Ai"] = "AI Assistant",
        ["Launcher_Group_Settings"] = "Settings",
        ["Launcher_TranslateQuery"] = "Translate \"{0}\"",
        ["Launcher_TranslateDescription"] = "Translate with AI",
        ["Launcher_AskQuery"] = "Ask AI \"{0}\"",
        ["Launcher_AskDescription"] = "Send single-turn question",
        ["Launcher_RecentApps"] = "Recent",
        ["Launcher_PinnedApps"] = "Pinned",
        ["Launcher_QuickActions"] = "Quick Actions",
        ["Launcher_QuickDirect"] = "Quick Direct",

        // SuperPanel
        ["SuperPanel_Title"] = "Pankin Super Panel",
        ["SuperPanel_ModeTranslate"] = "Auto Translate",
        ["SuperPanel_ModeChat"] = "AI Chat",
        ["SuperPanel_Translating"] = "Translating...",
        ["SuperPanel_TranslateError"] = "Translation failed: {0}",
        ["SuperPanel_NoResult"] = "(No result received, please check network or API Key)",
        ["SuperPanel_NoApiKey"] = "⚠️ API Key not configured. Please click ⚙️ to configure provider.",
        ["SuperPanel_CopyTranslation"] = "Copy Translation",
        ["SuperPanel_CopySource"] = "Copy Source",
        ["SuperPanel_SourceApp"] = "Source: {0}",
        ["SuperPanel_FilesSelected"] = "{0} files selected:",
        ["SuperPanel_ChatPlaceholder"] = "Type a message to AI (Enter to send)...",
        ["SuperPanel_Send"] = "Send",
        ["SuperPanel_ClearChat"] = "Clear Chat",
        ["SuperPanel_QuickSettings"] = "Quick Configure AI",
        ["SuperPanel_ApiBase"] = "Base URL:",
        ["SuperPanel_ApiKey"] = "Key:",
        ["SuperPanel_Model"] = "Model:",
        ["SuperPanel_Action_Copy"] = "Copy",
        ["SuperPanel_Action_Translate"] = "Translate",
        ["SuperPanel_Action_Chat"] = "Ask AI",
        ["SuperPanel_Action_Summary"] = "Summary",
        ["SuperPanel_Action_Explain"] = "Explain",
        ["SuperPanel_Action_Search"] = "Search",
        ["SuperPanel_Action_OpenLink"] = "Open Link",
        ["SuperPanel_Action_OpenPath"] = "Open Path",

        // Clipboard
        ["Clipboard_Title"] = "Pankin Clipboard History",
        ["Clipboard_SearchPlaceholder"] = "Search clipboard",
        ["Clipboard_ClearAll"] = "Clear all unpinned history",
        ["Clipboard_Empty"] = "No clipboard history yet",
        ["Clipboard_CopyTip"] = "Press Enter to paste, Del to delete",
        ["Clipboard_Pinned"] = "Pinned",
        ["Clipboard_Unpin"] = "Unpin",
        ["Clipboard_Pin"] = "Pin",
        ["Clipboard_Delete"] = "Delete",

        // Settings Navigation
        ["Settings_Title"] = "Pankin Settings",
        ["Settings_NavGeneral"] = "General",
        ["Settings_NavModules"] = "Modules",
        ["Settings_NavLauncher"] = "Launcher",
        ["Settings_NavClipboard"] = "Clipboard",
        ["Settings_NavGestures"] = "Gestures",
        ["Settings_NavSuperPanel"] = "Super Panel",
        ["Settings_NavFolderJump"] = "Folder Jump",
        ["Settings_NavBatchRename"] = "Batch Rename",
        ["Settings_NavPowerTimer"] = "Power Timer",
        ["Settings_NavAi"] = "AI Service",
        ["Settings_NavAbout"] = "About",

        // Settings General
        ["Settings_Startup"] = "Startup",
        ["Settings_StartAtLogin"] = "Start at Login",
        ["Settings_StartAtLogin_Description"] = "Automatically start Pankin in background on system login",
        ["Settings_ShowTrayIcon"] = "Show Tray Icon",
        ["Settings_ShowTrayIcon_Description"] = "Show tray icon in system tray bar",
        ["Settings_Appearance"] = "Appearance",
        ["Settings_Theme"] = "Theme",
        ["Settings_Theme_System"] = "System",
        ["Settings_Theme_Light"] = "Light",
        ["Settings_Theme_Dark"] = "Dark",
        ["Settings_Language"] = "Language",
        ["Settings_Language_System"] = "System",
        ["Settings_Language_ZhCn"] = "简体中文",
        ["Settings_Language_EnUs"] = "English",
        ["Settings_AccentColor"] = "Accent Color",
        ["Settings_PanelRadius"] = "Panel Corner Radius",
        ["Settings_PanelRadius_Small"] = "Compact (8px)",
        ["Settings_PanelRadius_Large"] = "Large (20px)",

        // Settings Launcher
        ["Settings_Launcher_Hotkey"] = "Launcher Hotkey",
        ["Settings_Launcher_DoubleTap"] = "Double-tap Ctrl to launch",
        ["Settings_Launcher_DoubleTap_Description"] = "Quickly press Control key twice to open Launcher",
        ["Settings_Launcher_SearchEngine"] = "Default Search Engine",

        // Settings Clipboard
        ["Settings_Clipboard_Hotkey"] = "Clipboard Hotkey",
        ["Settings_Clipboard_MaxItems"] = "History Max Items",
        ["Settings_Clipboard_RecordText"] = "Record Text",
        ["Settings_Clipboard_RecordImages"] = "Record Images",

        // Settings SuperPanel
        ["Settings_Super_Trigger"] = "Trigger Button",
        ["Settings_Super_HoldDuration"] = "Hold Duration",
        ["Settings_Super_AutoTranslate"] = "Auto-translate Selection",
        ["Settings_Super_AutoTranslate_Description"] = "Automatically translate foreign text when Super Panel opens",
        ["Settings_Super_MinLength"] = "Min Selection Length",
        ["Settings_Super_MaxLength"] = "Max Selection Length",
        ["Settings_Super_PauseFullscreen"] = "Pause in Fullscreen",
        ["Settings_Super_Blacklist"] = "App Blacklist",
        ["Settings_Super_Blacklist_Placeholder"] = "Enter app or process name...",

        // Settings AI
        ["Settings_Ai_Provider"] = "Provider",
        ["Settings_Ai_BaseUrl"] = "API Base URL",
        ["Settings_Ai_ApiKey"] = "API Key",
        ["Settings_Ai_Model"] = "Main Model",
        ["Settings_Ai_TranslationModel"] = "Translation Model",
        ["Settings_Ai_Format"] = "Protocol Format",
        ["Settings_Ai_Save"] = "Save Configuration",
        ["Settings_Ai_Test"] = "Test Connection",

        // Translate
        ["Translate_Title"] = "Translation",
        ["Translate_AutoDetect"] = "Auto Detect",
        ["Translate_InputPlaceholder"] = "Type or paste text to translate",
        ["Translate_Translating"] = "Translating...",
        ["Translate_LanguageChinese"] = "Chinese",
        ["Translate_LanguageEnglish"] = "English",
        ["Translate_Copied"] = "Translation copied",
        ["Translate_Failed"] = "Translation failed",

        // Tray
        ["Tray_Settings"] = "Settings",
        ["Tray_UpdateReady"] = "Pankin {0} is ready to update",
        ["Tray_CheckForUpdates"] = "Check for Updates",
        ["Launcher_Group_Favorites"] = "Favorites",

        // Modules & Updates
        ["Module_DependencyNotRunning"] = "Module '{0}' hard dependency is not running",
        ["Module_RestartPending"] = "This change requires restarting Pankin to take effect",
        ["Update_CheckTimeout"] = "Update check timed out",
        ["Update_DownloadTimeout"] = "Update download timed out",
        ["Update_RateLimited"] = "Request rate limited by GitHub, please try again later",
        ["Update_NetworkFailed"] = "Network connection failed, please check network settings",
        ["Update_StorageFailed"] = "Update cache read/write failed",
        ["Update_InvalidResponse"] = "Invalid server response format",
        ["Update_ReadyNotification"] = "Pankin {0} is ready to update",

        // Rename & FolderJump & Setup
        ["Rename_Title"] = "Batch Rename",
        ["Rename_NotFolder"] = "Specified path is not a folder",
        ["Rename_LinkedFolder"] = "Linked folders are not supported",
        ["Rename_Cancelled"] = "Operation was cancelled",
        ["Rename_PreviewInvalid"] = "Preview has conflicts or errors",
        ["Rename_RelatedRollback"] = "Related file operation failed, transaction rolled back",
        ["FolderJump_Title"] = "Folder Jump",
        ["Launcher_Group_FolderJump"] = "Folder Jump",
        ["Setup_Title"] = "First-Run Setup Wizard",
        ["SuperPanel_Action_BatchRename"] = "Batch Rename"
    };

    private static readonly Dictionary<string, string> ZhHantCatalog = new(StringComparer.Ordinal)
    {
        ["Tray_Settings"] = "設定",
        ["Common_Settings"] = "設定",
        ["Settings_Title"] = "Pankin 設定中心",
        ["Settings_NavGeneral"] = "通用",
        ["Settings_NavModules"] = "能力",
        ["Settings_NavLauncher"] = "啟動器",
        ["Settings_NavClipboard"] = "剪貼簿",
        ["Settings_NavGestures"] = "滑鼠手勢",
        ["Settings_NavSuperPanel"] = "超級面板",
        ["Settings_NavFolderJump"] = "資料夾跳轉",
        ["Settings_NavBatchRename"] = "批次重新命名",
        ["Settings_NavPowerTimer"] = "定時關機",
        ["Settings_NavAi"] = "AI 服務",
        ["Settings_NavAbout"] = "關於",
        ["Translate_LanguageChinese"] = "中文",
        ["Translate_LanguageEnglish"] = "英文",
        ["Tray_CheckForUpdates"] = "檢查更新",

        // Modules & Updates
        ["Module_DependencyNotRunning"] = "模組 '{0}' 的前置硬相依未在運行",
        ["Module_RestartPending"] = "此變更需要重啟 Pankin 後生效",
        ["Update_CheckTimeout"] = "檢查更新逾時",
        ["Update_DownloadTimeout"] = "下載更新逾時",
        ["Update_RateLimited"] = "請求過於頻繁，已被 GitHub 限制，請稍後重試",
        ["Update_NetworkFailed"] = "網路連線失敗，請檢查網路設定",
        ["Update_StorageFailed"] = "更新快取讀寫失敗",
        ["Update_InvalidResponse"] = "伺服器回應格式無效",
        ["Update_ReadyNotification"] = "Pankin {0} 已準備好更新",

        // Rename & FolderJump & Setup
        ["Rename_Title"] = "批次重新命名",
        ["Rename_NotFolder"] = "指定路徑不是資料夾",
        ["Rename_LinkedFolder"] = "不支援連結資料夾",
        ["Rename_Cancelled"] = "操作已被取消",
        ["Rename_PreviewInvalid"] = "預覽狀態不合法或存在衝突",
        ["Rename_RelatedRollback"] = "關聯檔案失敗，事務已復原",
        ["FolderJump_Title"] = "資料夾跳轉",
        ["Launcher_Group_FolderJump"] = "資料夾跳轉",
        ["Setup_Title"] = "首次運行配置精靈",
        ["SuperPanel_Action_BatchRename"] = "批次重新命名"
        // 其余自动回退到 zh-CN
    };

    #endregion
}
