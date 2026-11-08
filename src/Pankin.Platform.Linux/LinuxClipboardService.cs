using Pankin.Core.Selection;
using System.Diagnostics;
using System.Text;
using Pankin.Core.Abstractions;

namespace Pankin.Platform.Linux;

/// <summary>
/// 兼容 Ubuntu 24.04 Wayland / XWayland / X11 的剪贴板与划词服务实现。
/// 注意：在 GNOME Wayland (Ubuntu 默认桌面) 下，wl-paste 因为缺乏 wlr-data-control 协议支持，
/// 每次调用都会创建一个临时顶层窗口以获取焦点，如果在后台轮询会导致 Ubuntu Dock 栏不断有应用图标闪现又消失！
/// 因此当存在 DISPLAY (X11 / XWayland) 时，优先使用静默无窗口的 xclip，彻底消除 Dock 图标闪烁。
/// </summary>
public sealed class LinuxClipboardService : IClipboardService
{
    private readonly bool _hasX11Display;
    private readonly bool _hasWaylandDisplay;
    private readonly object _monitorLock = new();
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private string? _lastClipboardText;
    private bool _isDisposed;

    public event EventHandler<string>? ClipboardTextChanged;
#pragma warning disable CS0067
    public event EventHandler<string>? PrimarySelectionChanged;
#pragma warning restore CS0067

    public LinuxClipboardService()
    {
        _hasX11Display = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));
        _hasWaylandDisplay = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }

    public async Task<string?> GetTextAsync(CancellationToken ct = default)
    {
        // 优先走 xclip (无窗口、不会导致 GNOME Wayland Dock 图标闪现)
        if (_hasX11Display)
        {
            var text = await RunReadCommandAsync("xclip", ["-o", "-selection", "clipboard"], ct).ConfigureAwait(false);
            if (text != null) return text;
        }

        if (_hasWaylandDisplay)
        {
            return await RunReadCommandAsync("wl-paste", ["--no-newline"], ct).ConfigureAwait(false);
        }

        return null;
    }

    public async Task SetTextAsync(string text, CancellationToken ct = default)
    {
        if (text is null) return;

        if (_hasX11Display)
        {
            var ok = await RunWriteCommandAsync("xclip", ["-i", "-selection", "clipboard"], text, ct).ConfigureAwait(false);
            if (ok)
            {
                _lastClipboardText = text;
                return;
            }
        }

        if (_hasWaylandDisplay)
        {
            var ok = await RunWriteCommandAsync("wl-copy", [], text, ct).ConfigureAwait(false);
            if (ok)
            {
                _lastClipboardText = text;
            }
        }
    }

    public async Task<string?> GetPrimarySelectionTextAsync(CancellationToken ct = default)
    {
        // 优先使用 xclip 读取 primary selection，避免 wl-paste 弹窗抢焦点和闪 Dock 图标
        if (_hasX11Display)
        {
            var text = await RunReadCommandAsync("xclip", ["-o", "-selection", "primary"], ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        if (_hasWaylandDisplay)
        {
            var text = await RunReadCommandAsync("wl-paste", ["--primary", "--no-newline"], ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return null;
    }

    public async Task<ClipboardSnapshot> CreateSnapshotAsync(CancellationToken ct = default)
    {
        var text = await GetTextAsync(ct).ConfigureAwait(false);
        return new ClipboardSnapshot(text, DateTime.UtcNow);
    }

    public async Task RestoreSnapshotAsync(ClipboardSnapshot snapshot, CancellationToken ct = default)
    {
        if (snapshot?.Text != null)
        {
            await SetTextAsync(snapshot.Text, ct).ConfigureAwait(false);
        }
    }

    public async Task<string?> BorrowClipboardAsync(Func<Task<string?>> copyAction, CancellationToken ct = default)
    {
        ClipboardSnapshot? snapshot = null;
        try
        {
            snapshot = await CreateSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        try
        {
            return await copyAction().ConfigureAwait(false);
        }
        finally
        {
            if (snapshot != null)
            {
                try
                {
                    await RestoreSnapshotAsync(snapshot, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore restore errors
                }
            }
        }
    }

    public void StartMonitoring()
    {
        lock (_monitorLock)
        {
            if (_monitorCts != null) return;
            _monitorCts = new CancellationTokenSource();
            var token = _monitorCts.Token;
            _monitorTask = Task.Run(() => MonitorLoopAsync(token), token);
        }
    }

    public void StopMonitoring()
    {
        lock (_monitorLock)
        {
            if (_monitorCts == null) return;
            try
            {
                _monitorCts.Cancel();
                _monitorCts.Dispose();
            }
            catch
            {
                // Ignore
            }
            finally
            {
                _monitorCts = null;
                _monitorTask = null;
            }
        }
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        // 如果没有 X11/XWayland DISPLAY，不轮询 wl-paste，防止 GNOME Dock 图标闪现
        if (!_hasX11Display)
        {
            return;
        }

        try
        {
            _lastClipboardText = await RunReadCommandAsync("xclip", ["-o", "-selection", "clipboard"], ct).ConfigureAwait(false);
        }
        catch
        {
            // Ignore initial read error
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(900, ct).ConfigureAwait(false);

                var currentClip = await RunReadCommandAsync("xclip", ["-o", "-selection", "clipboard"], ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(currentClip) &&
                    !string.Equals(currentClip, _lastClipboardText, StringComparison.Ordinal))
                {
                    _lastClipboardText = currentClip;
                    ClipboardTextChanged?.Invoke(this, currentClip);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 忽略单次异常
            }
        }
    }

    private static async Task<string?> RunReadCommandAsync(string fileName, string[] args, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                }
            };

            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            if (!process.Start()) return null;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(1200);

            var output = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> RunWriteCommandAsync(string fileName, string[] args, string content, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                }
            };

            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            if (!process.Start()) return false;

            await process.StandardInput.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(800);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // xclip forks into background to serve X11 selection requests; closing stdin is sufficient
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        StopMonitoring();
    }
}
