using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pankin.Core.Abstractions;
using Pankin.Core.Gestures;
using Pankin.Core.Selection;
using SharpHook;
using SharpHook.Native;

namespace Pankin.Platform.Linux;

/// <summary>
/// 针对 Ubuntu 24.04 优化的全局快捷键与划词钩子（支持动态热键配置、GNOME Wayland 快捷键绑定与左键划词感知）。
/// </summary>
public sealed class LinuxInputHook : IGlobalInputHook
{
    private const int IpcUdpPort = 49182;

    private readonly IClipboardService _clipboardService;
    private readonly bool _ownsClipboardService;
    private TaskPoolGlobalHook? _hook;
    private Task? _hookTask;
    private UdpClient? _udpServer;
    private CancellationTokenSource? _ipcCts;
    private volatile bool _isRunning;
    private bool _isDisposed;

    private long _lastTriggerMs;

    private readonly object _gnomeRegLock = new();

    // 热键配置与解析
    private string _rawLauncherHotkey = "Ctrl+Space";
    private string _rawClipboardHotkey = "Alt+V";
    private string _rawSuperPanelHotkey = "Alt+S";
    private ParsedHotkey? _parsedLauncherHotkey;
    private ParsedHotkey? _parsedClipboardHotkey;
    private ParsedHotkey? _parsedSuperPanelHotkey;

    // 鼠标左键拖拽划词状态跟踪
    private volatile bool _leftMouseDown;
    private volatile bool _leftMouseDragged;
    private double _leftMouseDownX;
    private double _leftMouseDownY;
    private string? _lastSelectedText;
    private string? _baselinePrimaryText;
    private readonly SelectionDebounceFilter _selectionFilter = new();

    public bool SuperPanelEnabled { get; set; } = true;

    public event EventHandler<GlobalHotkeyEventArgs>? HotkeyTriggered;
    public event EventHandler<SelectionTriggeredEventArgs>? SelectionTriggered;
    public event EventHandler<GesturePoint>? GlobalPointerPressed;

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

        UpdateHotkeys("Ctrl+Space", "Alt+V", "Alt+S");
    }

    public void UpdateHotkeys(string launcherHotkey, string clipboardHotkey, string superPanelHotkey)
    {
        var normLauncher = string.IsNullOrWhiteSpace(launcherHotkey) ? "Ctrl+Space" : launcherHotkey.Trim();
        var normClipboard = string.IsNullOrWhiteSpace(clipboardHotkey) ? "Alt+V" : clipboardHotkey.Trim();
        var normSuperPanel = string.IsNullOrWhiteSpace(superPanelHotkey) ? "Alt+S" : superPanelHotkey.Trim();

        if (_parsedLauncherHotkey != null &&
            _parsedClipboardHotkey != null &&
            _parsedSuperPanelHotkey != null &&
            string.Equals(_rawLauncherHotkey, normLauncher, StringComparison.Ordinal) &&
            string.Equals(_rawClipboardHotkey, normClipboard, StringComparison.Ordinal) &&
            string.Equals(_rawSuperPanelHotkey, normSuperPanel, StringComparison.Ordinal))
        {
            return;
        }

        _rawLauncherHotkey = normLauncher;
        _rawClipboardHotkey = normClipboard;
        _rawSuperPanelHotkey = normSuperPanel;

        _parsedLauncherHotkey = ParseHotkey(_rawLauncherHotkey);
        _parsedClipboardHotkey = ParseHotkey(_rawClipboardHotkey);
        _parsedSuperPanelHotkey = ParseHotkey(_rawSuperPanelHotkey);

        if (_isRunning)
        {
            _ = Task.Run(RegisterGnomeWaylandShortcuts);
        }
    }

    public void ConfigureSelectionFilter(Pankin.Core.Settings.SuperPanelSettings settings)
    {
        if (settings == null) return;
        SuperPanelEnabled = settings.Enabled;
        _selectionFilter.TriggerOnMouseUp = settings.TriggerOnSelection && settings.TriggerOnMouseUp;
        _selectionFilter.TriggerOnDoubleClick = settings.TriggerOnSelection && settings.TriggerOnDoubleClick;
        _selectionFilter.MinLength = settings.SelectionMinLength;
        _selectionFilter.MaxLength = settings.SelectionMaxLength;
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
                HotkeyTriggered?.Invoke(this, new GlobalHotkeyEventArgs(GlobalHotkeyAction.ToggleLauncher, _rawLauncherHotkey));
                break;
            case "ToggleClipboard":
                HotkeyTriggered?.Invoke(this, new GlobalHotkeyEventArgs(GlobalHotkeyAction.ToggleClipboard, _rawClipboardHotkey));
                break;
            case "ToggleSuperPanel":
                HotkeyTriggered?.Invoke(this, new GlobalHotkeyEventArgs(GlobalHotkeyAction.ToggleSuperPanel, _rawSuperPanelHotkey));
                break;
            case "OpenSettings":
                HotkeyTriggered?.Invoke(this, new GlobalHotkeyEventArgs(GlobalHotkeyAction.OpenSettings, "Alt+,"));
                break;
        }
    }

    private void RegisterGnomeWaylandShortcuts()
    {
        lock (_gnomeRegLock)
        {
            try
            {
                var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
                if (!desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase) &&
                    !desktop.Contains("ubuntu", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var launcherBinding = _parsedLauncherHotkey?.GnomeBinding ?? "<Control>space";
                var clipboardBinding = _parsedClipboardHotkey?.GnomeBinding ?? "<Alt>v";
                var superPanelBinding = _parsedSuperPanelHotkey?.GnomeBinding ?? "<Alt>s";

                var existing = RunGsettings("get org.gnome.settings-daemon.plugins.media-keys custom-keybindings") ?? "[]";
                var slots = new (string Path, string Name, string Cmd, string Key)[]
                {
                    ("/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/pankin-launcher/", "Pankin Launcher", "ToggleLauncher", launcherBinding),
                    ("/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/pankin-clipboard/", "Pankin Clipboard", "ToggleClipboard", clipboardBinding),
                    ("/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/pankin-super/", "Pankin SuperPanel", "ToggleSuperPanel", superPanelBinding),
                    ("/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/pankin-settings/", "Pankin Settings", "OpenSettings", "<Alt>comma")
                };
                var validPaths = slots.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);

                var pathsList = new List<string>();
                var matches = System.Text.RegularExpressions.Regex.Matches(existing, @"'([^']+)'");
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    var p = m.Groups[1].Value;
                    if (string.IsNullOrWhiteSpace(p)) continue;

                    // 清理旧版本遗留或多余的 pankin 快捷键条目（如曾用的 pankin-clipboard2），避免抢占系统与终端快捷键
                    if (p.Contains("/pankin-", StringComparison.OrdinalIgnoreCase) && !validPaths.Contains(p))
                    {
                        RunGsettings($"reset-recursively org.gnome.settings-daemon.plugins.media-keys.custom-keybinding:{p}");
                        continue;
                    }

                    if (!pathsList.Contains(p))
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
                    var udpCmd = $"/bin/bash -c \"printf '%s' '{slot.Cmd}' > /dev/udp/127.0.0.1/{IpcUdpPort}\"";
                    ConfigureSingleGnomeShortcut(slot.Path, slot.Name, udpCmd, slot.Key);
                }
            }
            catch
            {
                // Ignore
            }
        }
    }

    private static void ConfigureSingleGnomeShortcut(string path, string name, string command, string binding)
    {
        var schemaWithPath = $"org.gnome.settings-daemon.plugins.media-keys.custom-keybinding:{path}";
        var escapedCommand = command.Replace("\"", "\\\"");
        RunGsettings($"set {schemaWithPath} name \"{name}\"");
        RunGsettings($"set {schemaWithPath} command \"{escapedCommand}\"");
        RunGsettings($"set {schemaWithPath} binding \"{binding}\"");
    }

    private static string? RunGsettings(string arguments)
    {
        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo
            {
                FileName = "gsettings",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc == null) return null;

            var readTask = proc.StandardOutput.ReadToEndAsync();
            if (proc.WaitForExit(1500) && readTask.Wait(200))
            {
                return proc.ExitCode == 0 ? readTask.Result.Trim() : null;
            }

            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (proc != null)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Ignore
                }
                proc.Dispose();
            }
        }
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        var key = e.Data.KeyCode;
        var mask = e.RawEvent.Mask;
        var now = Environment.TickCount64;

        if (Matches(mask, key, _parsedLauncherHotkey))
        {
            if (now - _lastTriggerMs >= 280)
            {
                _lastTriggerMs = now;
                HotkeyTriggered?.Invoke(this, new GlobalHotkeyEventArgs(GlobalHotkeyAction.ToggleLauncher, _rawLauncherHotkey));
            }
            return;
        }

        if (Matches(mask, key, _parsedClipboardHotkey))
        {
            if (now - _lastTriggerMs >= 280)
            {
                _lastTriggerMs = now;
                HotkeyTriggered?.Invoke(this, new GlobalHotkeyEventArgs(GlobalHotkeyAction.ToggleClipboard, _rawClipboardHotkey));
            }
            return;
        }

        if (Matches(mask, key, _parsedSuperPanelHotkey))
        {
            if (now - _lastTriggerMs >= 280)
            {
                _lastTriggerMs = now;
                HotkeyTriggered?.Invoke(this, new GlobalHotkeyEventArgs(GlobalHotkeyAction.ToggleSuperPanel, _rawSuperPanelHotkey));
            }
            return;
        }

        // 默认保留固定设置快捷键 Alt+,
        bool isAlt = (mask & (ModifierMask.LeftAlt | ModifierMask.RightAlt)) != 0;
        bool isCtrl = (mask & (ModifierMask.LeftCtrl | ModifierMask.RightCtrl)) != 0;
        bool isShift = (mask & (ModifierMask.LeftShift | ModifierMask.RightShift)) != 0;
        if (key == KeyCode.VcComma && isAlt && !isCtrl && !isShift)
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
        // 彻底移除双击 Ctrl 触发逻辑
    }

    private void OnMousePressed(object? sender, MouseHookEventArgs e)
    {
        GlobalPointerPressed?.Invoke(this, new GesturePoint(e.Data.X, e.Data.Y, Environment.TickCount64));

        var button = e.Data.Button;
        if (button == MouseButton.Button1)
        {
            _ = Task.Run(async () => { try { _baselinePrimaryText = await _clipboardService.GetPrimarySelectionTextAsync().ConfigureAwait(false); } catch { } });
        }

        if (!SuperPanelEnabled) return;

        if (button == MouseButton.Button1)
        {
            var now = Environment.TickCount64;
            _leftMouseDown = true;
            _leftMouseDragged = false;
            _leftMouseDownX = e.Data.X;
            _leftMouseDownY = e.Data.Y;
            _selectionFilter.RecordMouseDown(1, e.Data.X, e.Data.Y, now);
        }
    }

    private void OnMouseMoved(object? sender, MouseHookEventArgs e) => HandlePointerMove(e.Data.X, e.Data.Y);
    private void OnMouseDragged(object? sender, MouseHookEventArgs e) => HandlePointerMove(e.Data.X, e.Data.Y);

    private void HandlePointerMove(double x, double y)
    {
        if (!SuperPanelEnabled) return;

        if (_leftMouseDown && !_leftMouseDragged)
        {
            var dx = x - _leftMouseDownX;
            var dy = y - _leftMouseDownY;
            if (Math.Sqrt(dx * dx + dy * dy) > 18.0)
            {
                _leftMouseDragged = true;
            }
        }
    }

    private void OnMouseReleased(object? sender, MouseHookEventArgs e)
    {
        if (!SuperPanelEnabled) return;

        var button = e.Data.Button;
        if (button == MouseButton.Button1)
        {
            bool wasDragged = _leftMouseDown && _leftMouseDragged;
            _leftMouseDown = false;
            _leftMouseDragged = false;

            var decision = _selectionFilter.RecordMouseUp(1, e.Data.X, e.Data.Y, Environment.TickCount64, wasDragged);
            if (decision.ShouldTrigger)
            {
                _ = CaptureSelectionOnReleaseAsync(decision.X, decision.Y, decision.TriggerType);
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
            if (winInfo?.AppName != null && winInfo.AppName.Contains("pankin", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var text = await _clipboardService.GetPrimarySelectionTextAsync().ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(text))
            {
                var trimmed = text.Trim();
                if (_selectionFilter.IsValidLength(trimmed))
                {
                    if (string.Equals(trimmed, _lastSelectedText, StringComparison.Ordinal) ||
                        (triggerType == SelectionTriggerType.MouseDrag && string.Equals(trimmed, _baselinePrimaryText?.Trim(), StringComparison.Ordinal)))
                    {
                        return;
                    }

                    var now = Environment.TickCount64;
                    if (!_selectionFilter.IsDuplicate(trimmed, now))
                    {
                        _selectionFilter.RecordCapture(trimmed, now);
                        _lastSelectedText = trimmed;
                        _baselinePrimaryText = trimmed;
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

    private static bool Matches(ModifierMask mask, KeyCode key, ParsedHotkey? hotkey)
    {
        if (hotkey == null) return false;
        if (key != hotkey.KeyCode) return false;

        bool isCtrl = (mask & (ModifierMask.LeftCtrl | ModifierMask.RightCtrl)) != 0;
        bool isAlt = (mask & (ModifierMask.LeftAlt | ModifierMask.RightAlt)) != 0;
        bool isShift = (mask & (ModifierMask.LeftShift | ModifierMask.RightShift)) != 0;
        bool isMeta = (mask & (ModifierMask.LeftMeta | ModifierMask.RightMeta)) != 0;

        return isCtrl == hotkey.Ctrl &&
               isAlt == hotkey.Alt &&
               isShift == hotkey.Shift &&
               isMeta == hotkey.Meta;
    }

    private static ParsedHotkey? ParseHotkey(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return null;

        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;

        bool ctrl = false;
        bool alt = false;
        bool shift = false;
        bool meta = false;
        string? keyPart = null;

        foreach (var part in parts)
        {
            var lower = part.ToLowerInvariant();
            if (lower is "ctrl" or "control")
            {
                ctrl = true;
            }
            else if (lower is "alt" or "opt" or "option")
            {
                alt = true;
            }
            else if (lower is "shift")
            {
                shift = true;
            }
            else if (lower is "super" or "win" or "windows" or "meta")
            {
                meta = true;
            }
            else
            {
                keyPart = part;
            }
        }

        if (string.IsNullOrEmpty(keyPart)) return null;

        var (keyCode, gnomeKey) = MapKeyPart(keyPart);
        if (keyCode == KeyCode.VcUndefined) return null;

        var gnomeBinding = new StringBuilder();
        if (ctrl) gnomeBinding.Append("<Control>");
        if (alt) gnomeBinding.Append("<Alt>");
        if (shift) gnomeBinding.Append("<Shift>");
        if (meta) gnomeBinding.Append("<Super>");
        gnomeBinding.Append(gnomeKey);

        return new ParsedHotkey(hotkey, ctrl, alt, shift, meta, keyCode, gnomeBinding.ToString());
    }

    private static (KeyCode KeyCode, string GnomeKey) MapKeyPart(string part)
    {
        var lower = part.ToLowerInvariant();
        return lower switch
        {
            "space" => (KeyCode.VcSpace, "space"),
            "," or "comma" => (KeyCode.VcComma, "comma"),
            "." or "period" => (KeyCode.VcPeriod, "period"),
            "/" or "slash" => (KeyCode.VcSlash, "slash"),
            "\\" or "backslash" => (KeyCode.VcBackslash, "backslash"),
            "-" or "minus" => (KeyCode.VcMinus, "minus"),
            "=" or "equals" or "equal" => (KeyCode.VcEquals, "equal"),
            "[" or "openbracket" => (KeyCode.VcOpenBracket, "bracketleft"),
            "]" or "closebracket" => (KeyCode.VcCloseBracket, "bracketright"),
            "'" or "quote" => (KeyCode.VcQuote, "apostrophe"),
            "`" or "backquote" or "grave" => (KeyCode.VcBackQuote, "grave"),
            ";" or "semicolon" => (KeyCode.VcSemicolon, "semicolon"),
            "tab" => (KeyCode.VcTab, "Tab"),
            "enter" or "return" => (KeyCode.VcEnter, "Return"),
            "esc" or "escape" => (KeyCode.VcEscape, "Escape"),
            "backspace" => (KeyCode.VcBackspace, "BackSpace"),
            _ => MapAlphaNumericOrFunction(part)
        };
    }

    private static (KeyCode KeyCode, string GnomeKey) MapAlphaNumericOrFunction(string part)
    {
        var upper = part.ToUpperInvariant();
        var lower = part.ToLowerInvariant();

        if (System.Text.RegularExpressions.Regex.IsMatch(upper, @"^F\d{1,2}$"))
        {
            if (Enum.TryParse<KeyCode>("Vc" + upper, out var fKey))
            {
                return (fKey, upper);
            }
        }

        if (part.Length == 1 && char.IsAsciiDigit(part[0]))
        {
            if (Enum.TryParse<KeyCode>("Vc" + part, out var dKey))
            {
                return (dKey, part);
            }
        }

        if (part.Length == 1 && char.IsAsciiLetter(part[0]))
        {
            if (Enum.TryParse<KeyCode>("Vc" + char.ToUpperInvariant(part[0]), out var aKey))
            {
                return (aKey, lower);
            }
        }

        return (KeyCode.VcUndefined, lower);
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

    private sealed record ParsedHotkey(
        string Raw,
        bool Ctrl,
        bool Alt,
        bool Shift,
        bool Meta,
        KeyCode KeyCode,
        string GnomeBinding
    );
}
