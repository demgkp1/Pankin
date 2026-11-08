using System.Diagnostics;
using Pankin.Core.Abstractions;

namespace Pankin.Platform.Linux;

/// <summary>
/// 基于 Ubuntu 24.04 Linux 底层 CLI/进程交互的启动器动作执行宿主 (gio/dbus-free 纯 CLI 实现)
/// </summary>
public sealed class LinuxLauncherActionHost : ILauncherActionHost
{
    private static readonly string[] TerminalCandidates =
    [
        "x-terminal-emulator",
        "gnome-terminal",
        "ptyxis",
        "konsole",
        "xfce4-terminal",
        "alacritty",
        "kitty",
        "foot",
        "terminator",
        "xterm"
    ];

    private readonly IClipboardService? _clipboardService;
    private string? _detectedTerminal;

    public LinuxLauncherActionHost(IClipboardService? clipboardService = null)
    {
        _clipboardService = clipboardService;
    }

    public async Task<bool> OpenAsync(string pathOrUrl, bool elevated = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) return false;

        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                bool isUrl = pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                if (elevated && !isUrl)
                {
                    if (!File.Exists(pathOrUrl) && !Directory.Exists(pathOrUrl))
                    {
                        var cleanCommand = StripDesktopFieldCodes(pathOrUrl);
                        psi.FileName = "pkexec";
                        psi.ArgumentList.Add("/bin/sh");
                        psi.ArgumentList.Add("-c");
                        psi.ArgumentList.Add(cleanCommand);
                    }
                    else
                    {
                        psi.FileName = "pkexec";
                        psi.ArgumentList.Add("xdg-open");
                        psi.ArgumentList.Add(pathOrUrl);
                    }
                }
                else
                {
                    psi.FileName = "xdg-open";
                    psi.ArgumentList.Add(pathOrUrl);
                }

                using var proc = Process.Start(psi);
                return proc != null;
            }
            catch
            {
                return false;
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> RevealAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        return await Task.Run(() =>
        {
            try
            {
                string dir = Directory.Exists(path)
                    ? path
                    : Path.GetDirectoryName(path) ?? "/";

                // 优先尝试使用常见文件管理器的高亮选中参数
                if (File.Exists(path))
                {
                    if (IsCommandAvailable("nautilus"))
                    {
                        var psi = new ProcessStartInfo("nautilus") { UseShellExecute = false, CreateNoWindow = true };
                        psi.ArgumentList.Add("--select");
                        psi.ArgumentList.Add(path);
                        using var proc = Process.Start(psi);
                        if (proc != null) return true;
                    }
                    else if (IsCommandAvailable("dolphin"))
                    {
                        var psi = new ProcessStartInfo("dolphin") { UseShellExecute = false, CreateNoWindow = true };
                        psi.ArgumentList.Add("--select");
                        psi.ArgumentList.Add(path);
                        using var proc = Process.Start(psi);
                        if (proc != null) return true;
                    }
                    else if (IsCommandAvailable("nemo"))
                    {
                        var psi = new ProcessStartInfo("nemo") { UseShellExecute = false, CreateNoWindow = true };
                        psi.ArgumentList.Add("--no-desktop");
                        psi.ArgumentList.Add(path);
                        using var proc = Process.Start(psi);
                        if (proc != null) return true;
                    }
                }

                // 兜底直接打开所在目录
                var fallbackPsi = new ProcessStartInfo("xdg-open") { UseShellExecute = false, CreateNoWindow = true };
                fallbackPsi.ArgumentList.Add(dir);
                using var fallback = Process.Start(fallbackPsi);
                return fallback != null;
            }
            catch
            {
                return false;
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> RunInTerminalAsync(string command, bool elevated = false, string? workingDir = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        return await Task.Run(() =>
        {
            try
            {
                var terminal = DetectTerminal();
                var workDir = !string.IsNullOrWhiteSpace(workingDir) && Directory.Exists(workingDir)
                    ? workingDir
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                var finalCommand = elevated
                    ? $"pkexec bash -c \"{command.Replace("\"", "\\\"")}; exec bash\""
                    : $"{command}; exec bash";

                var psi = new ProcessStartInfo
                {
                    FileName = terminal,
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var termName = Path.GetFileName(terminal).ToLowerInvariant();
                switch (termName)
                {
                    case "gnome-terminal":
                    case "ptyxis":
                        psi.ArgumentList.Add($"--working-directory={workDir}");
                        psi.ArgumentList.Add("--");
                        psi.ArgumentList.Add("bash");
                        psi.ArgumentList.Add("-c");
                        psi.ArgumentList.Add(finalCommand);
                        break;

                    case "konsole":
                        psi.ArgumentList.Add($"--workdir");
                        psi.ArgumentList.Add(workDir);
                        psi.ArgumentList.Add("-e");
                        psi.ArgumentList.Add("bash");
                        psi.ArgumentList.Add("-c");
                        psi.ArgumentList.Add(finalCommand);
                        break;

                    case "xfce4-terminal":
                        psi.ArgumentList.Add($"--working-directory={workDir}");
                        psi.ArgumentList.Add("-e");
                        psi.ArgumentList.Add($"bash -c '{finalCommand}'");
                        break;

                    case "alacritty":
                        psi.ArgumentList.Add("--working-directory");
                        psi.ArgumentList.Add(workDir);
                        psi.ArgumentList.Add("-e");
                        psi.ArgumentList.Add("bash");
                        psi.ArgumentList.Add("-c");
                        psi.ArgumentList.Add(finalCommand);
                        break;

                    default: // x-terminal-emulator, xterm 等通用参数
                        psi.ArgumentList.Add("-e");
                        psi.ArgumentList.Add("bash");
                        psi.ArgumentList.Add("-c");
                        psi.ArgumentList.Add(finalCommand);
                        break;
                }

                using var proc = Process.Start(psi);
                return proc != null;
            }
            catch
            {
                return false;
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> CopyTextAsync(string text, CancellationToken ct = default)
    {
        if (_clipboardService != null)
        {
            try
            {
                await _clipboardService.SetTextAsync(text, ct).ConfigureAwait(false);
                return true;
            }
            catch { }
        }

        return await Task.Run(() =>
        {
            try
            {
                // 检测 Wayland 剪贴板 wl-copy
                if (IsCommandAvailable("wl-copy"))
                {
                    using var proc = new Process
                    {
                        StartInfo = new ProcessStartInfo("wl-copy")
                        {
                            RedirectStandardInput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    if (proc.Start())
                    {
                        proc.StandardInput.Write(text);
                        proc.StandardInput.Close();
                        proc.WaitForExit(1000);
                        return true;
                    }
                }

                // 检测 X11 剪贴板 xclip
                if (IsCommandAvailable("xclip"))
                {
                    using var proc = new Process
                    {
                        StartInfo = new ProcessStartInfo("xclip", "-selection clipboard")
                        {
                            RedirectStandardInput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    if (proc.Start())
                    {
                        proc.StandardInput.Write(text);
                        proc.StandardInput.Close();
                        proc.WaitForExit(1000);
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> ExecuteSystemCommandAsync(string command, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                string shellScript = command.ToLowerInvariant() switch
                {
                    "lock" => "loginctl lock-session || gnome-screensaver-command -l || xdg-screensaver lock",
                    "sleep" => "systemctl suspend",
                    "restart" => "systemctl reboot",
                    "shutdown" => "systemctl poweroff",
                    "logout" or "logoff" => "loginctl terminate-session self || gnome-session-quit --logout --no-prompt",
                    "recycle" or "trash" => "gio trash --empty 2>/dev/null || rm -rf ~/.local/share/Trash/files/* ~/.local/share/Trash/info/*",
                    _ => string.Empty
                };

                if (string.IsNullOrEmpty(shellScript)) return false;

                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(shellScript);

                using var proc = Process.Start(psi);
                return proc != null;
            }
            catch
            {
                return false;
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> ShowPropertiesAsync(string path, CancellationToken ct = default)
    {
        // Linux 桌面无跨环境的标准属性窗，回退为在文件管理器中选中定位
        return await RevealAsync(path, ct).ConfigureAwait(false);
    }

    private string DetectTerminal()
    {
        if (_detectedTerminal != null) return _detectedTerminal;

        foreach (var candidate in TerminalCandidates)
        {
            if (IsCommandAvailable(candidate))
            {
                _detectedTerminal = candidate;
                return candidate;
            }
        }

        _detectedTerminal = "/bin/bash";
        return _detectedTerminal;
    }

    private static bool IsCommandAvailable(string command)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin:/usr/local/bin";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var fullPath = Path.Combine(dir.Trim(), command);
            if (File.Exists(fullPath)) return true;
        }
        return false;
    }

    private static string StripDesktopFieldCodes(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return string.Empty;
        var cleaned = System.Text.RegularExpressions.Regex.Replace(command, @"%[fFuUidDnNckvm]", "");
        return System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();
    }
}
