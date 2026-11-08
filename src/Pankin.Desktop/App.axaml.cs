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
    private GestureOverlayWindow? _gestureOverlayWindow;

    private MainViewModel? _mainVm;
    private ClipboardViewModel? _clipboardVm;
    private SuperPanelViewModel? _superPanelVm;
    private SettingsViewModel? _settingsVm;

    private readonly EventSimulator _simulator = new();
    private long _lastCtrlUpTimeMs;
    private bool _ctrlPressedSolo;
    private long _lastWindowActionMs;

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

            _gestureOverlayWindow = new GestureOverlayWindow();

            desktop.MainWindow = _mainWindow;

            // 联动窗口关闭/隐藏与跨窗口呼出事件
            _mainVm.RequestHideWindow += () => _mainWindow.Hide();
            _mainVm.RequestOpenClipboard += () => ToggleClipboardWindow();
            _mainVm.RequestOpenSettings += tabIndex => ShowSettings(tabIndex);
            _mainVm.RequestOpenSuperPanel += (text, mode) => ShowSuperPanel(text, mode: mode);

            _clipboardVm.RequestHideWindow += () => _clipboardWindow.Hide();
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

            var context = DesktopAppContext.Instance;
            if (!context.SettingsStore.Current.General.SetupCompleted)
            {
                var setupVm = new SetupViewModel(context.SetupWizardService, context.AutostartService, context.SettingsStore);
                var setupWindow = new SetupWindow
                {
                    DataContext = setupVm,
                    Icon = appIcon
                };
                setupWindow.Show();
            }
            else
            {
                _mainWindow.Show();
            }
        }

        base.OnFrameworkInitializationCompleted();
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
        InputElement.KeyUpEvent.AddClassHandler<Window>(OnAnyWindowKeyUp, RoutingStrategies.Tunnel);
    }

    private void OnAnyWindowKeyDown(Window window, KeyEventArgs e)
    {
        bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool isAlt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // 如果按下了除 Ctrl 以外的任何键，则本次 Ctrl 单独敲击作废
        if (e.Key != Key.LeftCtrl && e.Key != Key.RightCtrl)
        {
            _ctrlPressedSolo = false;
        }
        else
        {
            _ctrlPressedSolo = true;
        }

        var modules = DesktopAppContext.Instance.SettingsStore.Current.Modules;

        // Ctrl+Space 或 Alt+Space -> 主启动器
        if ((isCtrl && e.Key == Key.Space) || (isAlt && e.Key == Key.Space))
        {
            if (modules.Launcher)
            {
                ToggleMainWindow();
                e.Handled = true;
                return;
            }
        }

        // Alt+V 或 Ctrl+Shift+V -> 剪贴板历史
        if ((isAlt && e.Key == Key.V) || (isCtrl && isShift && e.Key == Key.V))
        {
            if (modules.Clipboard)
            {
                ToggleClipboardWindow();
                e.Handled = true;
                return;
            }
        }

        // Alt+S -> 超级面板
        if (isAlt && e.Key == Key.S)
        {
            if (modules.SuperPanel)
            {
                ShowSuperPanel();
                e.Handled = true;
                return;
            }
        }

        // Alt+, (Key.OemComma) -> 设置
        if (isAlt && (e.Key == Key.OemComma || e.Key == Key.OemPeriod))
        {
            ShowSettings();
            e.Handled = true;
            return;
        }
    }

    private void OnAnyWindowKeyUp(Window window, KeyEventArgs e)
    {
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
        {
            if (_ctrlPressedSolo)
            {
                long now = Environment.TickCount64;
                long diff = now - _lastCtrlUpTimeMs;
                if (diff is > 50 and <= 420)
                {
                    _lastCtrlUpTimeMs = 0;
                    _ctrlPressedSolo = false;
                    var settings = DesktopAppContext.Instance.SettingsStore.Current;
                    if (settings.Modules.Launcher && settings.Launcher.DoubleTapCtrl)
                    {
                        ToggleMainWindow();
                        e.Handled = true;
                        return;
                    }
                }
                _lastCtrlUpTimeMs = now;
            }
        }
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, WindowIcon appIcon)
    {
        var menu = new NativeMenu();

        var itemLauncher = new NativeMenuItem("主启动器 (Alt+Space)");
        itemLauncher.Click += (_, _) => ToggleMainWindow();
        menu.Add(itemLauncher);

        var itemClipboard = new NativeMenuItem("剪贴板历史 (Alt+V)");
        itemClipboard.Click += (_, _) => ToggleClipboardWindow();
        menu.Add(itemClipboard);

        var itemSuper = new NativeMenuItem("超级面板 & AI 对话 (Alt+S)");
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

        hook.GestureProgress += (_, e) =>
        {
            if (!DesktopAppContext.Instance.SettingsStore.Current.Modules.Gestures) return;
            if (!DesktopAppContext.Instance.SettingsStore.Current.Gestures.ShowTrail) return;

            if (!e.IsActive)
            {
                _gestureOverlayWindow?.HideOverlay();
                return;
            }

            string action = GetGestureActionName(e.CurrentDirection);
            _gestureOverlayWindow?.UpdateProgress(e.CurrentX, e.CurrentY, e.CurrentDirection, action, e.Trajectory);
        };

        hook.GestureRecognized += (_, e) =>
        {
            _gestureOverlayWindow?.HideOverlay();
            if (!DesktopAppContext.Instance.SettingsStore.Current.Modules.Gestures) return;

            if (e.GestureDirection == "Hold" || !string.IsNullOrEmpty(e.GestureDirection))
            {
                string targetAction = GetGestureActionName(e.GestureDirection);
                ExecuteGestureAction(targetAction, e.EndX, e.EndY);
            }
        };

        hook.SelectionTriggered += (_, e) =>
        {
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

                string mode = settings.Ai.AutoTranslateSelection ? "translate" : "action";
                ShowSuperPanel(context: ctx, mode: mode);
            }
        };
    }

    private static string GetGestureActionName(string direction)
    {
        var bindings = DesktopAppContext.Instance.SettingsStore.Current.Gestures.Bindings;
        foreach (var b in bindings)
        {
            if (string.Equals(b.Direction, direction, StringComparison.OrdinalIgnoreCase))
            {
                return b.Action;
            }
        }

        return direction switch
        {
            "L" => "后退 (Alt+Left)",
            "R" => "前进 (Alt+Right)",
            "U" => "回到顶部 (Home)",
            "D" => "到底部 (End)",
            "DR" => "关闭标签页 (Ctrl+W)",
            "UL" => "呼出启动器",
            "UR" => "呼出剪贴板",
            "Hold" => "呼出超级面板",
            _ => "无动作"
        };
    }

    private void ExecuteGestureAction(string action, double endX, double endY)
    {
        if (string.IsNullOrWhiteSpace(action) || action == "无动作") return;

        if (action.Contains("启动器"))
        {
            ToggleMainWindow();
        }
        else if (action.Contains("剪贴板"))
        {
            ToggleClipboardWindow();
        }
        else if (action.Contains("超级面板"))
        {
            ShowSuperPanel(x: endX, y: endY);
        }
        else if (action.Contains("设置"))
        {
            ShowSettings();
        }
        else if (action.Contains("锁定屏幕"))
        {
            try
            {
                Process.Start(new ProcessStartInfo("loginctl", "lock-session") { UseShellExecute = false, CreateNoWindow = true });
            }
            catch { }
        }
        else
        {
            SimulateShortcut(action);
        }
    }

    private void SimulateShortcut(string action)
    {
        try
        {
            if (action.Contains("Alt+Left"))
            {
                _simulator.SimulateKeyPress(KeyCode.VcLeftAlt);
                _simulator.SimulateKeyPress(KeyCode.VcLeft);
                _simulator.SimulateKeyRelease(KeyCode.VcLeft);
                _simulator.SimulateKeyRelease(KeyCode.VcLeftAlt);
            }
            else if (action.Contains("Alt+Right"))
            {
                _simulator.SimulateKeyPress(KeyCode.VcLeftAlt);
                _simulator.SimulateKeyPress(KeyCode.VcRight);
                _simulator.SimulateKeyRelease(KeyCode.VcRight);
                _simulator.SimulateKeyRelease(KeyCode.VcLeftAlt);
            }
            else if (action.Contains("Home"))
            {
                _simulator.SimulateKeyPress(KeyCode.VcHome);
                _simulator.SimulateKeyRelease(KeyCode.VcHome);
            }
            else if (action.Contains("End"))
            {
                _simulator.SimulateKeyPress(KeyCode.VcEnd);
                _simulator.SimulateKeyRelease(KeyCode.VcEnd);
            }
            else if (action.Contains("Ctrl+W"))
            {
                _simulator.SimulateKeyPress(KeyCode.VcLeftControl);
                _simulator.SimulateKeyPress(KeyCode.VcW);
                _simulator.SimulateKeyRelease(KeyCode.VcW);
                _simulator.SimulateKeyRelease(KeyCode.VcLeftControl);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[App] Failed to simulate gesture key '{action}': {ex.Message}");
        }
    }

    public void ToggleMainWindow()
    {
        long now = Environment.TickCount64;
        if (now - _lastWindowActionMs < 320) return;
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

                _mainWindow.Show();
                _mainWindow.Topmost = false;
                _mainWindow.Topmost = true;
                _mainWindow.Activate();
                _mainWindow.FocusSearchInput();
            }
        });
    }

    public void ToggleClipboardWindow()
    {
        long now = Environment.TickCount64;
        if (now - _lastWindowActionMs < 320) return;
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
            }
        });
    }

    public void ShowSuperPanel(string? text = null, double? x = null, double? y = null, string mode = "translate", SelectionContext? context = null)
    {
        long now = Environment.TickCount64;
        if (now - _lastWindowActionMs < 280) return;
        _lastWindowActionMs = now;

        Dispatcher.UIThread.Post(() =>
        {
            if (_superPanelWindow == null) return;
            _mainWindow?.Hide();
            _clipboardWindow?.Hide();

            var ctx = context ?? new SelectionContext(
                Text: text,
                Files: null,
                SourceAppName: null,
                WindowTitle: null,
                PointerX: x ?? (_mainWindow != null ? _mainWindow.Position.X + 120 : 400),
                PointerY: y ?? (_mainWindow != null ? _mainWindow.Position.Y + 80 : 300),
                Timestamp: DateTime.UtcNow,
                Failure: string.IsNullOrWhiteSpace(text) ? SelectionFailure.Empty : SelectionFailure.None);

            _superPanelWindow.ShowNearPointer(ctx, mode);
            _superPanelWindow.Topmost = false;
            _superPanelWindow.Topmost = true;
            _superPanelWindow.Activate();
        });
    }

    public void ShowSettings(int tabIndex = 0)
    {
        long now = Environment.TickCount64;
        if (now - _lastWindowActionMs < 320) return;
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
