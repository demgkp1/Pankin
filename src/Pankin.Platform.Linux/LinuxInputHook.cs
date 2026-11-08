using Pankin.Core.Selection;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Pankin.Core.Abstractions;
using Pankin.Core.Gestures;
using SharpHook;
using SharpHook.Native;

namespace Pankin.Platform.Linux;

/// <summary>
/// 针对 Ubuntu 24.04 深度优化的全局快捷键、鼠标手势（支持右键拖拽/中键/侧键 + 实时轨迹事件）与划词钩子。
/// </summary>
public sealed class LinuxInputHook : IGlobalInputHook
{
    private const int IpcUdpPort = 49182;
    private const long DoubleTapThresholdMs = 450;

    private readonly IClipboardService _clipboardService;
    private readonly bool _ownsClipboardService;
    private TaskPoolGlobalHook? _hook;
    private Task? _hookTask;
    private UdpClient? _udpServer;
    private CancellationTokenSource? _ipcCts;
    private volatile bool _isRunning;
    private bool _isDisposed;

    // 双击 Ctrl 状态跟踪（基于事件 Mask 与时间戳，不依赖易卡死的布尔锁存）
    private long _lastNonCtrlKeyMs;
    private long _lastCtrlDownMs;
    private long _lastCtrlReleaseMs;
    private long _lastTriggerMs;

    // 鼠标手势状态跟踪（支持右键 Button2、中键 Button3、侧键 Button4/Button5）
    private readonly object _gestureLock = new();
    private bool _isGestureCandidate;
    private bool _isGestureDragging;
    private MouseButton _activeGestureButton;
    private long _gestureStartTime;
    private double _gestureStartX;
    private double _gestureStartY;
    private readonly List<GesturePoint> _gestureTrajectory = [];

    // 鼠标左键拖拽划词状态跟踪
    private volatile bool _leftMouseDown;
    private volatile bool _leftMouseDragged;
    private double _leftMouseDownX;
    private double _leftMouseDownY;
    private string? _lastSelectedText;
    private readonly SelectionDebounceFilter _selectionFilter = new();

    public event EventHandler<GlobalHotkeyEventArgs>? HotkeyTriggered;
    public event EventHandler<MouseGestureProgressEventArgs>? GestureProgress;
    public event EventHandler<MouseGestureEventArgs>? GestureRecognized;
    public event EventHandler<SelectionTriggeredEventArgs>? SelectionTriggered;

    public LinuxInputHook(IClipboardService? clipboardService = null)
    {
        if (clipboardService != null)
        {
            _clipboardService = clipboardService;
            _ownsClipboardService = false;
        }
        else
        {
            _clipboardService = new LinuxClipboardService();
            _ownsClipboardService = true;
        }
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning || _isDisposed) return Task.CompletedTask;
        _isRunning = true;

        StartLocalIpcAndGnomeShortcuts();

        try
        {
            _hook = new TaskPoolGlobalHook();
            _hook.KeyPressed += OnKeyPressed;
            _hook.KeyReleased += OnKeyReleased;
            _hook.MousePressed += OnMousePressed;
            _hook.MouseReleased += OnMouseReleased;
            _hook.MouseMoved += OnMouseMoved;
            _hook.MouseDragged += OnMouseDragged;

            _hookTask = _hook.RunAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LinuxInputHook] Warning: Failed to start SharpHook: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (!_isRunning) return Task.CompletedTask;
        _isRunning = false;

        try
        {
            _ipcCts?.Cancel();
            _udpServer?.Close();
            _udpServer?.Dispose();
            _udpServer = null;
        }
        catch
        {
            // Ignore
        }

        if (_hook != null)
        {
            try
            {
                _hook.KeyPressed -= OnKeyPressed;
                _hook.KeyReleased -= OnKeyReleased;
                _hook.MousePressed -= OnMousePressed;
                _hook.MouseReleased -= OnMouseReleased;
                _hook.MouseMoved -= OnMouseMoved;
                _hook.MouseDragged -= OnMouseDragged;
                _hook.Dispose();
                _hook = null;
            }
            catch
            {
                // Ignore
            }
        }

        return Task.CompletedTask;
    }

    private void StartLocalIpcAndGnomeShortcuts()
    {
        try
        {
            _ipcCts = new CancellationTokenSource();
            _udpServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, IpcUdpPort));
            var token = _ipcCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await _udpServer.ReceiveAsync(token).ConfigureAwait(false);
                        var cmd = Encoding.UTF8.GetString(result.Buffer).Trim();
                        DispatchCommandString(cmd);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }, token);

            _ = Task.Run(RegisterGnomeWaylandShortcuts);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LinuxInputHook] UDP IPC init note: {ex.Message}");
        }
    }

    private void DispatchCommandString(string cmd)
    {
        var now = Environment.TickCount64;
        if (now - _lastTriggerMs < 280) return;
        _lastTriggerMs = now;

        switch (cmd)
        {
            case "ToggleLauncher":
                HotkeyTriggered?.Invoke(this, new GlobalHotkeyEventArgs(GlobalHotkeyAction.ToggleLauncher, "Ctrl+Space"));
                break;
            case "ToggleClipboard":
                HotkeyTriggered?.Invoke(this, new GlobalHotkeyEventArgs(GlobalHotkeyAction.ToggleClipboard, "Alt+V"));
                break;
            case "ToggleSuperPanel":
                HotkeyTriggered?.Invoke(this, new GlobalHotkeyEventArgs(GlobalHotkeyAction.ToggleSuperPanel, "Alt+S"));
                break;
            case "OpenSettings":
                HotkeyTriggered?.Invoke(this, new GlobalHotkeyEventArgs(GlobalHotkeyAction.OpenSettings, "Alt+,"));
                break;
        }
    }

    private static void RegisterGnomeWaylandShortcuts()
    {
        try
        {
            var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
            if (!desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase) &&
                !desktop.Contains("ubuntu", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var existing = RunGsettings("get org.gnome.settings-daemon.plugins.media-keys custom-keybindings") ?? "[]";
            var slots = new (string Path, string Name, string Cmd, string Key)[]
            {
                ("/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/pankin-launcher/", "Pankin Launcher", "ToggleLauncher", "<Control>space"),
                ("/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/pankin-clipboard/", "Pankin Clipboard", "ToggleClipboard", "<Alt>v"),
                ("/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/pankin-clipboard2/", "Pankin Clipboard CtrlShiftV", "ToggleClipboard", "<Control><Shift>v"),
                ("/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/pankin-super/", "Pankin SuperPanel", "ToggleSuperPanel", "<Alt>s"),
                ("/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/pankin-settings/", "Pankin Settings", "OpenSettings", "<Alt>comma")
            };

            var pathsList = new List<string>();
            var matches = System.Text.RegularExpressions.Regex.Matches(existing, @"'([^']+)'");
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                var p = m.Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(p) && !pathsList.Contains(p))
                {
                    pathsList.Add(p);
                }
            }

            foreach (var slot in slots)
            {
                if (!pathsList.Contains(slot.Path))
                {
                    pathsList.Add(slot.Path);
                }
            }

            var arrayLiteral = "[" + string.Join(", ", pathsList.Select(p => $"'{p}'")) + "]";
            RunGsettings($"set org.gnome.settings-daemon.plugins.media-keys custom-keybindings \"{arrayLiteral}\"");

            foreach (var slot in slots)
            {
                var pyCmd = $"python3 -c \"import socket; socket.socket(socket.AF_INET, socket.SOCK_DGRAM).sendto(b'{slot.Cmd}', ('127.0.0.1', {IpcUdpPort}))\"";
                ConfigureSingleGnomeShortcut(slot.Path, slot.Name, pyCmd, slot.Key);
            }
        }
        catch
        {
            // Ignore
        }
    }

    private static void ConfigureSingleGnomeShortcut(string path, string name, string command, string binding)
    {
        var schemaWithPath = $"org.gnome.settings-daemon.plugins.media-keys.custom-keybinding:{path}";
        RunGsettings($"set {schemaWithPath} name \"{name}\"");
        RunGsettings($"set {schemaWithPath} command \"{command}\"");
        RunGsettings($"set {schemaWithPath} binding \"{binding}\"");
    }

    private static string? RunGsettings(string arguments)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "gsettings",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(1500);
            return proc.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        var key = e.Data.KeyCode;
        var mask = e.RawEvent.Mask;
        var now = Environment.TickCount64;

        if (key is KeyCode.VcLeftControl or KeyCode.VcRightControl)
        {
            if (now - _lastCtrlDownMs > 250)
            {
                _lastCtrlDownMs = now;
            }
            return;
        }

        _lastNonCtrlKeyMs = now;

        bool isAlt = (mask & (ModifierMask.LeftAlt | ModifierMask.RightAlt)) != 0;
        bool isCtrl = (mask & (ModifierMask.LeftCtrl | ModifierMask.RightCtrl)) != 0;
        bool isShift = (mask & (ModifierMask.LeftShift | ModifierMask.RightShift)) != 0;

        if (key == KeyCode.VcSpace && (isCtrl || isAlt))
        {
            if (now - _lastTriggerMs >= 280)
            {
                _lastTriggerMs = now;
                HotkeyTriggered?.Invoke(this, new GlobalHotkeyEventArgs(GlobalHotkeyAction.ToggleLauncher, isCtrl ? "Ctrl+Space" : "Alt+Space"));
            }
            return;
        }

        if (key == KeyCode.VcV && (isAlt || (isCtrl && isShift)))
        {
            if (now - _lastTriggerMs >= 280)
            {
                _lastTriggerMs = now;
                HotkeyTriggered?.Invoke(this, new GlobalHotkeyEventArgs(GlobalHotkeyAction.ToggleClipboard, isAlt ? "Alt+V" : "Ctrl+Shift+V"));
            }
            return;
        }

        if (key == KeyCode.VcS && isAlt)
        {
            if (now - _lastTriggerMs >= 280)
            {
                _lastTriggerMs = now;
                HotkeyTriggered?.Invoke(this, new GlobalHotkeyEventArgs(GlobalHotkeyAction.ToggleSuperPanel, "Alt+S"));
            }
            return;
        }

        if (key == KeyCode.VcComma && isAlt)
        {
            if (now - _lastTriggerMs >= 280)
            {
                _lastTriggerMs = now;
                HotkeyTriggered?.Invoke(this, new GlobalHotkeyEventArgs(GlobalHotkeyAction.OpenSettings, "Alt+,"));
            }
        }
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        var key = e.Data.KeyCode;
        var now = Environment.TickCount64;

        if (key is KeyCode.VcLeftControl or KeyCode.VcRightControl)
        {
            if (now - _lastNonCtrlKeyMs > 300)
            {
                if (now - _lastCtrlReleaseMs <= DoubleTapThresholdMs && (now - _lastCtrlReleaseMs) >= 40)
                {
                    _lastCtrlReleaseMs = 0;
                    if (now - _lastTriggerMs >= 280)
                    {
                        _lastTriggerMs = now;
                        HotkeyTriggered?.Invoke(this, new GlobalHotkeyEventArgs(GlobalHotkeyAction.ToggleLauncher, "双击 Ctrl"));
                    }
                }
                else
                {
                    _lastCtrlReleaseMs = now;
                }
            }
        }
    }

    private void OnMousePressed(object? sender, MouseHookEventArgs e)
    {
        var now = Environment.TickCount64;
        _lastNonCtrlKeyMs = now;
        var button = e.Data.Button;
        double x = e.Data.X;
        double y = e.Data.Y;

        if (button == MouseButton.Button1)
        {
            _leftMouseDown = true;
            _leftMouseDragged = false;
            _leftMouseDownX = x;
            _leftMouseDownY = y;
            _selectionFilter.RecordMouseDown(1, x, y, now);
            return;
        }

        if (button is MouseButton.Button2 or MouseButton.Button3 or MouseButton.Button4 or MouseButton.Button5)
        {
            lock (_gestureLock)
            {
                _isGestureCandidate = true;
                _isGestureDragging = false;
                _activeGestureButton = button;
                _gestureStartTime = now;
                _gestureStartX = x;
                _gestureStartY = y;
                _gestureTrajectory.Clear();
                _gestureTrajectory.Add(new GesturePoint(x, y, now));
            }
        }
    }

    private void OnMouseMoved(object? sender, MouseHookEventArgs e) => HandlePointerMove(e.Data.X, e.Data.Y);
    private void OnMouseDragged(object? sender, MouseHookEventArgs e) => HandlePointerMove(e.Data.X, e.Data.Y);

    private void HandlePointerMove(double x, double y)
    {
        if (_leftMouseDown && !_leftMouseDragged)
        {
            var dx = x - _leftMouseDownX;
            var dy = y - _leftMouseDownY;
            if (Math.Sqrt(dx * dx + dy * dy) > 18.0)
            {
                _leftMouseDragged = true;
            }
        }

        List<GesturePoint>? snapshot = null;
        lock (_gestureLock)
        {
            if (_isGestureCandidate)
            {
                var now = Environment.TickCount64;
                _gestureTrajectory.Add(new GesturePoint(x, y, now));
                var dist = Math.Sqrt((x - _gestureStartX) * (x - _gestureStartX) + (y - _gestureStartY) * (y - _gestureStartY));
                if (dist >= 24.0)
                {
                    _isGestureDragging = true;
                }

                if (_isGestureDragging)
                {
                    snapshot = [.. _gestureTrajectory];
                }
            }
        }

        if (snapshot != null)
        {
            var dir = GestureRecognizer.Recognize(snapshot, minimumDistance: 24.0) ?? "";
            GestureProgress?.Invoke(this, new MouseGestureProgressEventArgs(true, dir, snapshot, x, y));
        }
    }

    private void OnMouseReleased(object? sender, MouseHookEventArgs e)
    {
        var button = e.Data.Button;
        double x = e.Data.X;
        double y = e.Data.Y;

        if (button == MouseButton.Button1)
        {
            bool wasDragged = _leftMouseDown && _leftMouseDragged;
            _leftMouseDown = false;
            _leftMouseDragged = false;

            var decision = _selectionFilter.RecordMouseUp(1, x, y, Environment.TickCount64, wasDragged);
            if (decision.ShouldTrigger)
            {
                _ = CaptureSelectionOnReleaseAsync(decision.X, decision.Y, decision.TriggerType);
            }
            return;
        }

        List<GesturePoint>? points = null;
        long duration = 0;
        bool wasDragging = false;
        MouseButton releasedBtn = MouseButton.NoButton;

        lock (_gestureLock)
        {
            if (_isGestureCandidate && button == _activeGestureButton)
            {
                _isGestureCandidate = false;
                wasDragging = _isGestureDragging;
                _isGestureDragging = false;
                releasedBtn = _activeGestureButton;
                var now = Environment.TickCount64;
                duration = now - _gestureStartTime;
                _gestureTrajectory.Add(new GesturePoint(x, y, now));
                points = [.. _gestureTrajectory];
                _gestureTrajectory.Clear();
            }
        }

        if (points != null)
        {
            GestureProgress?.Invoke(this, new MouseGestureProgressEventArgs(false, "", [], x, y));

            var start = points[0];
            var end = points[^1];
            var recognized = wasDragging ? GestureRecognizer.Recognize(points, minimumDistance: 28.0) : null;

            if (!string.IsNullOrEmpty(recognized))
            {
                GestureRecognized?.Invoke(this, new MouseGestureEventArgs(
                    recognized, points, start.X, start.Y, end.X, end.Y));
            }
            else if (!wasDragging && releasedBtn is MouseButton.Button4 or MouseButton.Button5 or MouseButton.Button3 && duration >= 260)
            {
                GestureRecognized?.Invoke(this, new MouseGestureEventArgs(
                    "Hold", points, start.X, start.Y, end.X, end.Y));
            }
        }
    }

    private async Task CaptureSelectionOnReleaseAsync(double mouseX, double mouseY, SelectionTriggerType triggerType = SelectionTriggerType.MouseDrag)
    {
        try
        {
            int delayMs = triggerType == SelectionTriggerType.DoubleClick ? 120 : 60;
            await Task.Delay(delayMs).ConfigureAwait(false);

            var winInfo = await LinuxWindowInfoResolver.GetActiveWindowInfoAsync().ConfigureAwait(false);
            var text = await _clipboardService.GetPrimarySelectionTextAsync().ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(text))
            {
                var trimmed = text.Trim();
                if (_selectionFilter.IsValidLength(trimmed))
                {
                    var now = Environment.TickCount64;
                    if (!_selectionFilter.IsDuplicate(trimmed, now))
                    {
                        _selectionFilter.RecordCapture(trimmed, now);
                        _lastSelectedText = trimmed;
                        SelectionTriggered?.Invoke(this, new SelectionTriggeredEventArgs(
                            trimmed,
                            mouseX,
                            mouseY,
                            winInfo?.AppName,
                            winInfo?.Title));
                    }
                }
            }
        }
        catch
        {
            // Ignore
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _ = StopAsync();
        if (_ownsClipboardService)
        {
            _clipboardService.Dispose();
        }
    }
}
