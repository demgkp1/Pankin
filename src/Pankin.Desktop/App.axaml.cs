using Pankin.Core.Selection;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Pankin.Core.Abstractions;
using Pankin.Desktop.Services;
using Pankin.Desktop.ViewModels;
using Pankin.Desktop.Views;
using SharpHook;
using SharpHook.Native;

namespace Pankin.Desktop;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private ClipboardWindow? _clipboardWindow;
    private SuperPanelWindow? _superPanelWindow;
    private SettingsWindow? _settingsWindow;

    private MainViewModel? _mainVm;
    private ClipboardViewModel? _clipboardVm;
    private SuperPanelViewModel? _superPanelVm;
    private SettingsViewModel? _settingsVm;

    private long _lastWindowActionMs;
    private long _mainWindowShownMs;
    private long _clipboardWindowShownMs;
    private long _superPanelWindowShownMs;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var appIcon = CreateAppIcon();

            _mainVm = new MainViewModel();
            _mainWindow = new MainWindow
            {
                DataContext = _mainVm,
                Icon = appIcon
            };

            _clipboardVm = new ClipboardViewModel();
            _clipboardWindow = new ClipboardWindow
            {
                DataContext = _clipboardVm,
                Icon = appIcon
            };

            _superPanelVm = new SuperPanelViewModel();
            _superPanelWindow = new SuperPanelWindow
            {
                DataContext = _superPanelVm,
                Icon = appIcon
            };

            _settingsVm = new SettingsViewModel();
            _settingsWindow = new SettingsWindow
            {
                DataContext = _settingsVm,
                Icon = appIcon
            };

            desktop.MainWindow = _mainWindow;

            // 应用全局外观设置
            ApplyAppearance(DesktopAppContext.Instance.SettingsStore.Current.General);
            DesktopAppContext.Instance.SettingsStore.Changed += (_, s) =>
            {
                Dispatcher.UIThread.Post(() => ApplyAppearance(s.General));
            };
            this.ActualThemeVariantChanged += (_, _) => ApplyAppearance(DesktopAppContext.Instance.SettingsStore.Current.General);

            // 联动窗口关闭/隐藏与跨窗口呼出事件
            _mainVm.RequestHideWindow += () => _mainWindow.Hide();
            _mainVm.RequestOpenClipboard += () => ToggleClipboardWindow(bypassThrottle: true);
            _mainVm.RequestOpenSettings += tabIndex => ShowSettings(tabIndex, bypassThrottle: true);
            _mainVm.RequestOpenSuperPanel += (text, mode) => ShowSuperPanel(text, mode: mode, bypassThrottle: true);

            _clipboardVm.RequestHideWindow += () => _clipboardWindow.Hide();
            _clipboardVm.RequestSendToLauncher += text =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_mainVm != null) _mainVm.SearchQuery = text;
                    if (_mainWindow != null && !_mainWindow.IsVisible)
                    {
                        ToggleMainWindow(bypassThrottle: true);
                    }
                    else
                    {
                        _mainWindow?.Activate();
                        _mainWindow?.FocusSearchInput();
                    }
                });
            };

            _superPanelVm.RequestHideWindow += () => _superPanelWindow.Hide();
            _superPanelVm.RequestOpenRename += files => ShowRenameWindow(files);

            // 拦截所有应用窗口内的全局热键
            RegisterGlobalWindowKeyHandlers();

            // 挂载托盘图标
            SetupTrayIcon(desktop, appIcon);

            // 监听全局输入钩子事件
            WireGlobalInputHooks();

            // 异步初始化底层服务并展示主界面/首次向导
            _ = InitializeDesktopContextAsync();

            // 彻底移除首次运行向导，直接就绪常驻后台并唤起主启动器
            var context = DesktopAppContext.Instance;
            if (!context.SettingsStore.Current.General.SetupCompleted)
            {
                var updated = context.SettingsStore.Current with
                {
                    General = context.SettingsStore.Current.General with { SetupCompleted = true }
                };
                context.SettingsStore.Save(updated);
            }

            ApplySavedMainWindowPosition();
            _mainWindow.Show();
            _mainWindow.Activate();
            _mainWindow.FocusSearchInput();
            _mainWindowShownMs = Environment.TickCount64;
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyAppearance(Pankin.Core.Settings.GeneralSettings general)
    {
        if (Current == null) return;

        // 1. Theme Variant
        Current.RequestedThemeVariant = general.Theme switch
        {
            Pankin.Core.Settings.ThemeMode.Light => Avalonia.Styling.ThemeVariant.Light,
            Pankin.Core.Settings.ThemeMode.Dark => Avalonia.Styling.ThemeVariant.Dark,
            _ => Avalonia.Styling.ThemeVariant.Default
        };

        bool isLight = general.Theme == Pankin.Core.Settings.ThemeMode.Light || (general.Theme == Pankin.Core.Settings.ThemeMode.System && Current.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light);

        // 2. Opacity
        byte alpha = (byte)Math.Clamp((int)Math.Round(general.PanelOpacity * 255.0), 102, 255);
        byte r = isLight ? (byte)251 : (byte)20;
        byte g = isLight ? (byte)251 : (byte)20;
        byte b = isLight ? (byte)252 : (byte)23;
        var surfaceBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(alpha, r, g, b));
        Current.Resources["Pk.Surface"] = surfaceBrush;
        // Keep the saved opacity as the tint alpha. MainWindow adds a separate
        // readability shield only when the backend does not report background blur.
        Current.Resources["Pk.LauncherSurface"] = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromArgb(alpha, isLight ? (byte)245 : (byte)48,
                isLight ? (byte)244 : (byte)49, isLight ? (byte)242 : (byte)53));

        // 3. Accent Color
        if (Avalonia.Media.Color.TryParse(general.AccentColor, out var accentColor))
        {
            var accentBrush = new Avalonia.Media.SolidColorBrush(accentColor);
            Current.Resources["Pk.Accent"] = accentBrush;

            byte strongR = (byte)Math.Max(0, accentColor.R * 0.85);
            byte strongG = (byte)Math.Max(0, accentColor.G * 0.85);
            byte strongB = (byte)Math.Max(0, accentColor.B * 0.85);
            Current.Resources["Pk.AccentStrong"] = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, strongR, strongG, strongB));

            Current.Resources["Pk.AccentSoft"] = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x2B, accentColor.R, accentColor.G, accentColor.B));
            Current.Resources["Pk.AccentSofter"] = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x17, accentColor.R, accentColor.G, accentColor.B));

            byte textR = isLight ? (byte)Math.Max(0, accentColor.R * 0.75) : (byte)Math.Min(255, accentColor.R * 1.3);
            byte textG = isLight ? (byte)Math.Max(0, accentColor.G * 0.75) : (byte)Math.Min(255, accentColor.G * 1.3);
            byte textB = isLight ? (byte)Math.Max(0, accentColor.B * 0.75) : (byte)Math.Min(255, accentColor.B * 1.3);
            Current.Resources["Pk.AccentText"] = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, textR, textG, textB));
        }

        // 4. Panel Radius
        Current.Resources["Pk.Radius.Window"] = new CornerRadius(Math.Max(0, general.PanelRadius));
    }

    private async Task InitializeDesktopContextAsync()
    {
        try
        {
            await DesktopAppContext.Instance.InitializeAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => _mainVm?.LoadDefaultApps());

            if (DesktopAppContext.Instance.SettingsStore.Current.Update.CheckOnStartup)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(3000).ConfigureAwait(false);
                        await DesktopAppContext.Instance.UpdateService.CheckAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { }
                });
            }
        }
        catch { }
    }

    private void RegisterGlobalWindowKeyHandlers()
    {
        InputElement.KeyDownEvent.AddClassHandler<Window>(OnAnyWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnAnyWindowKeyDown(Window window, KeyEventArgs e)
    {
        if (window is SettingsWindow && _settingsVm is { RecordingTarget: not null }) return;

        var s = DesktopAppContext.Instance.SettingsStore.Current;
        var modules = s.Modules;

        if (modules.Launcher && MatchesAvaloniaKey(e, s.Launcher.Hotkey))
        {
            ToggleMainWindow();
            e.Handled = true;
            return;
        }

        if (modules.Clipboard && MatchesAvaloniaKey(e, s.Clipboard.Hotkey))
        {
            ToggleClipboardWindow();
            e.Handled = true;
            return;
        }

        if (modules.SuperPanel && MatchesAvaloniaKey(e, s.SuperPanel.Hotkey))
        {
            ShowSuperPanel();
            e.Handled = true;
            return;
        }

        bool isAlt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (isAlt && (e.Key == Key.OemComma || e.Key == Key.OemPeriod))
        {
            ShowSettings();
            e.Handled = true;
            return;
        }
    }

    private static bool MatchesAvaloniaKey(KeyEventArgs e, string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return false;
        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool reqCtrl = false, reqAlt = false, reqShift = false, reqMeta = false;
        string? mainKey = null;

        foreach (var p in parts)
        {
            var l = p.ToLowerInvariant();
            if (l is "ctrl" or "control") reqCtrl = true;
            else if (l is "alt" or "option") reqAlt = true;
            else if (l is "shift") reqShift = true;
            else if (l is "super" or "win" or "meta") reqMeta = true;
            else mainKey = p;
        }

        bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool isAlt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool isMeta = e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        if (isCtrl != reqCtrl || isAlt != reqAlt || isShift != reqShift || isMeta != reqMeta)
            return false;

        if (string.IsNullOrEmpty(mainKey)) return false;

        if (string.Equals(mainKey, "space", StringComparison.OrdinalIgnoreCase))
            return e.Key == Key.Space;

        if (Enum.TryParse<Key>(mainKey, true, out var parsedKey))
            return e.Key == parsedKey;

        return false;
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, WindowIcon appIcon)
    {
        var menu = new NativeMenu();

        var itemLauncher = new NativeMenuItem("主启动器");
        itemLauncher.Click += (_, _) => ToggleMainWindow();
        menu.Add(itemLauncher);

        var itemClipboard = new NativeMenuItem("剪贴板历史");
        itemClipboard.Click += (_, _) => ToggleClipboardWindow();
        menu.Add(itemClipboard);

        var itemSuper = new NativeMenuItem("超级面板 & AI 对话");
        itemSuper.Click += (_, _) => ShowSuperPanel();
        menu.Add(itemSuper);

        var itemSettings = new NativeMenuItem("系统设置 (Alt+,)");
        itemSettings.Click += (_, _) => ShowSettings();
        menu.Add(itemSettings);

        var itemUpdate = new NativeMenuItem("检查更新");
        itemUpdate.Click += async (_, _) =>
        {
            ShowSettings(10);
            await DesktopAppContext.Instance.UpdateService.CheckAsync(System.Threading.CancellationToken.None);
        };
        menu.Add(itemUpdate);

        menu.Add(new NativeMenuItemSeparator());

        var itemScan = new NativeMenuItem("重新扫描桌面应用");
        itemScan.Click += async (_, _) =>
        {
            await DesktopAppContext.Instance.AppScanner.ScanAppsAsync();
            _mainVm?.LoadDefaultApps();
        };
        menu.Add(itemScan);

        menu.Add(new NativeMenuItemSeparator());

        var itemExit = new NativeMenuItem("退出 Pankin");
        itemExit.Click += (_, _) =>
        {
            DesktopAppContext.Instance.Dispose();
            desktop.Shutdown();
        };
        menu.Add(itemExit);

        var trayIcon = new TrayIcon
        {
            Icon = appIcon,
            ToolTipText = "Pankin - Linux 桌面效率工具",
            IsVisible = true,
            Menu = menu
        };
        trayIcon.Clicked += (_, _) => ToggleMainWindow();

        var trayIcons = new TrayIcons { trayIcon };
        TrayIcon.SetIcons(this, trayIcons);

        DesktopAppContext.Instance.UpdateService.Changed += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var update = DesktopAppContext.Instance.UpdateService;
                if (update.State is Pankin.Core.Updates.UpdateState.Available or Pankin.Core.Updates.UpdateState.Ready)
                {
                    trayIcon.ToolTipText = $"Pankin - {string.Format(Pankin.Core.Localization.Strings.Get("Tray_UpdateReady"), update.Latest?.Version)}";
                }
            });
        };
    }

    public void ShowRenameWindow(System.Collections.Generic.IEnumerable<string>? files = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var vm = new RenameViewModel(DesktopAppContext.Instance.BatchRenamer);
            if (files != null)
            {
                vm.AddFiles(files);
            }

            var win = new RenameWindow
            {
                DataContext = vm,
                Icon = CreateAppIcon()
            };
            win.Show();
        });
    }

    private void WireGlobalInputHooks()
    {
        var hook = DesktopAppContext.Instance.InputHook;

        hook.HotkeyTriggered += (_, e) =>
        {
            var modules = DesktopAppContext.Instance.SettingsStore.Current.Modules;
            switch (e.Action)
            {
                case GlobalHotkeyAction.ToggleLauncher:
                    if (modules.Launcher) ToggleMainWindow();
                    break;
                case GlobalHotkeyAction.ToggleClipboard:
                    if (modules.Clipboard) ToggleClipboardWindow();
                    break;
                case GlobalHotkeyAction.ToggleSuperPanel:
                    if (modules.SuperPanel) ShowSuperPanel();
                    break;
                case GlobalHotkeyAction.OpenSettings:
                    ShowSettings();
                    break;
            }
        };

        hook.GlobalPointerPressed += (_, pt) =>
        {
            if (!DesktopAppContext.Instance.SettingsStore.Current.General.HideOnDeactivate) return;

            Dispatcher.UIThread.Post(() =>
            {
                long now = Environment.TickCount64;

                void CheckAndHide(Window? win, long shownMs)
                {
                    if (win != null && win.IsVisible && (now - shownMs > 250))
                    {
                        var pos = win.Position;
                        var bounds = win.Bounds;
                        bool inside = pt.X >= pos.X && pt.X <= pos.X + bounds.Width &&
                                      pt.Y >= pos.Y && pt.Y <= pos.Y + bounds.Height;
                        if (!inside)
                        {
                            win.Hide();
                        }
                    }
                }

                CheckAndHide(_mainWindow, _mainWindowShownMs);
                CheckAndHide(_clipboardWindow, _clipboardWindowShownMs);
                CheckAndHide(_superPanelWindow, _superPanelWindowShownMs);
            });
        };

        hook.SelectionTriggered += (_, e) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_mainWindow?.IsActive == true || _clipboardWindow?.IsActive == true ||
                    _superPanelWindow?.IsActive == true || _settingsWindow?.IsActive == true)
                {
                    return;
                }

                Window?[] openWindows = [_mainWindow, _clipboardWindow, _superPanelWindow, _settingsWindow];
                foreach (var win in openWindows)
                {
                    if (win != null && win.IsVisible)
                    {
                        if (win.Position.X <= e.MouseX && e.MouseX <= win.Position.X + win.Bounds.Width &&
                            win.Position.Y <= e.MouseY && e.MouseY <= win.Position.Y + win.Bounds.Height)
                        {
                            return;
                        }
                    }
                }

                var settings = DesktopAppContext.Instance.SettingsStore.Current;
                if (settings.Modules.SuperPanel && settings.SuperPanel.Enabled && settings.SuperPanel.TriggerOnSelection)
                {
                    // 黑名单过滤（基于前台应用进程名或窗口标题）
                    if (settings.SuperPanel.BlacklistedApps.Count > 0 &&
                        SelectionDebounceFilter.IsBlacklisted(e.SourceAppName, e.WindowTitle, settings.SuperPanel.BlacklistedApps))
                    {
                        return;
                    }

                    var ctx = new SelectionContext(
                        Text: e.SelectedText,
                        Files: e.Files,
                        SourceAppName: e.SourceAppName,
                        WindowTitle: e.WindowTitle,
                        PointerX: e.MouseX,
                        PointerY: e.MouseY,
                        Timestamp: DateTime.UtcNow,
                        Failure: SelectionFailure.None);

                    string mode = (!settings.Modules.Ai || !settings.Ai.Enabled || !settings.Ai.AutoTranslateSelection)
                        ? "action"
                        : "translate";
                    ShowSuperPanel(context: ctx, mode: mode);
                }
            });
        };
    }

    private void ApplySavedMainWindowPosition()
    {
        if (_mainWindow == null) return;
        var general = DesktopAppContext.Instance.SettingsStore.Current.General;
        if (general.RememberWindowPosition &&
            general.LauncherPositionX.HasValue &&
            general.LauncherPositionY.HasValue)
        {
            var posX = general.LauncherPositionX.Value;
            var posY = general.LauncherPositionY.Value;

            try
            {
                var screen = _mainWindow.Screens.ScreenFromPoint(new PixelPoint(posX, posY))
                             ?? _mainWindow.Screens.Primary;
                if (screen != null)
                {
                    var wa = screen.WorkingArea;
                    int winW = (int)(_mainWindow.Width > 0 ? _mainWindow.Width : 704);
                    int winH = (int)(_mainWindow.Height > 0 ? _mainWindow.Height : 544);

                    int clampedX = Math.Clamp(posX, wa.X, Math.Max(wa.X, wa.X + wa.Width - winW));
                    int clampedY = Math.Clamp(posY, wa.Y, Math.Max(wa.Y, wa.Y + wa.Height - winH));

                    _mainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                    _mainWindow.Position = new PixelPoint(clampedX, clampedY);
                    return;
                }
            }
            catch
            {
                // 忽略多屏探测异常
            }

            _mainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            _mainWindow.Position = new PixelPoint(posX, posY);
        }
        else
        {
            _mainWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    public void ToggleMainWindow(bool bypassThrottle = false)
    {
        long now = Environment.TickCount64;
        if (!bypassThrottle && now - _lastWindowActionMs < 320) return;
        _lastWindowActionMs = now;

        Dispatcher.UIThread.Post(() =>
        {
            if (_mainWindow == null) return;
            if (_mainWindow.IsVisible)
            {
                _mainWindow.Hide();
            }
            else
            {
                _clipboardWindow?.Hide();
                _superPanelWindow?.Hide();

                ApplySavedMainWindowPosition();

                _mainWindow.Show();
                _mainWindow.Topmost = false;
                _mainWindow.Topmost = true;
                _mainWindow.Activate();
                _mainWindow.FocusSearchInput();
                _mainWindowShownMs = Environment.TickCount64;
            }
        });
    }

    public void ToggleClipboardWindow(bool bypassThrottle = false)
    {
        long now = Environment.TickCount64;
        if (!bypassThrottle && now - _lastWindowActionMs < 320) return;
        _lastWindowActionMs = now;

        Dispatcher.UIThread.Post(() =>
        {
            if (_clipboardWindow == null) return;
            if (_clipboardWindow.IsVisible)
            {
                _clipboardWindow.Hide();
            }
            else
            {
                _mainWindow?.Hide();
                _superPanelWindow?.Hide();

                _clipboardVm?.ReloadItems();
                _clipboardWindow.Show();
                _clipboardWindow.Topmost = false;
                _clipboardWindow.Topmost = true;
                _clipboardWindow.Activate();
                _clipboardWindow.FocusSearchInput();
                _clipboardWindowShownMs = Environment.TickCount64;
            }
        });
    }

    public void ShowSuperPanel(string? text = null, double? x = null, double? y = null, string mode = "translate", SelectionContext? context = null, bool bypassThrottle = false)
    {
        long now = Environment.TickCount64;
        if (!bypassThrottle && now - _lastWindowActionMs < 280) return;
        _lastWindowActionMs = now;

        _ = Task.Run(async () =>
        {
            string? resolvedText = text;
            if (context == null && string.IsNullOrWhiteSpace(resolvedText))
            {
                try
                {
                    var clipService = DesktopAppContext.Instance.ClipboardService;
                    resolvedText = await clipService.GetPrimarySelectionTextAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(resolvedText) &&
                        DesktopAppContext.Instance.SettingsStore.Current.SuperPanel.ClipboardFallback)
                    {
                        resolvedText = await clipService.GetTextAsync().ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Ignore
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_superPanelWindow == null) return;
                _mainWindow?.Hide();
                _clipboardWindow?.Hide();

                var ctx = context ?? new SelectionContext(
                    Text: resolvedText,
                    Files: null,
                    SourceAppName: null,
                    WindowTitle: null,
                    PointerX: x ?? (_mainWindow != null ? _mainWindow.Position.X + 120 : 400),
                    PointerY: y ?? (_mainWindow != null ? _mainWindow.Position.Y + 80 : 300),
                    Timestamp: DateTime.UtcNow,
                    Failure: string.IsNullOrWhiteSpace(resolvedText) ? SelectionFailure.Empty : SelectionFailure.None);

                _superPanelWindow.ShowNearPointer(ctx, mode);
                _superPanelWindow.Topmost = false;
                _superPanelWindow.Topmost = true;
                _superPanelWindow.Activate();
                _superPanelWindowShownMs = Environment.TickCount64;
            });
        });
    }

    public void ShowSettings(int tabIndex = 0, bool bypassThrottle = false)
    {
        long now = Environment.TickCount64;
        if (!bypassThrottle && now - _lastWindowActionMs < 320) return;
        _lastWindowActionMs = now;

        Dispatcher.UIThread.Post(() =>
        {
            if (_settingsWindow == null) return;
            _settingsWindow.SelectTab(tabIndex);
            _settingsWindow.Show();
            _settingsWindow.Topmost = false;
            _settingsWindow.Topmost = true;
            _settingsWindow.Activate();
        });
    }

    private static WindowIcon CreateAppIcon()
    {
        var pixelSize = new PixelSize(32, 32);
        var dpi = new Vector(96, 96);
        var bitmap = new WriteableBitmap(pixelSize, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bitmap.Lock())
        {
            int[] pixels = new int[32 * 32];
            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 32; x++)
                {
                    bool inCircle = (x - 16) * (x - 16) + (y - 16) * (y - 16) <= 14 * 14;
                    if (inCircle)
                    {
                        int color = unchecked((int)0xFFE5881E); // Blue badge background

                        bool isStem = x >= 10 && x <= 13 && y >= 8 && y <= 24;
                        bool isTop = x >= 13 && x <= 20 && y >= 8 && y <= 11;
                        bool isMid = x >= 13 && x <= 20 && y >= 14 && y <= 17;
                        bool isLoopRight = x >= 18 && x <= 21 && y >= 11 && y <= 14;

                        if (isStem || isTop || isMid || isLoopRight)
                        {
                            color = unchecked((int)0xFFFFFFFF); // White P letter
                        }

                        pixels[y * 32 + x] = color;
                    }
                    else
                    {
                        pixels[y * 32 + x] = 0; // Transparent
                    }
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        }
        return new WindowIcon(bitmap);
    }
}
