using Pankin.Core.Selection;
using System.Diagnostics;
using System.Text;
using Pankin.Core.Abstractions;

namespace Pankin.Platform.Linux;

/// <summary>
/// 兼容 Ubuntu 24.04 Wayland / XWayland / X11 的剪贴板与划词服务实现。
/// 桌面端可注入原生剪贴板访问，无需 xclip/wl-clipboard 外部依赖。
/// 后台不使用 wl-paste 抢焦点的 GNOME 回退；独立调用仍可使用命令行后端。
/// </summary>
public sealed class LinuxClipboardService : IClipboardService
{
    private readonly bool _hasX11Display;
    private readonly bool _hasWaylandDisplay;
    private readonly bool _isGnomeWayland;
    private Func<CancellationToken, Task<string?>>? _desktopTextReader;
    private Func<string, CancellationToken, Task>? _desktopTextWriter;
    private readonly SemaphoreSlim _nativeClipboardAccess = new(1, 1);
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
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
        _isGnomeWayland = _hasWaylandDisplay && (desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase) ||
            desktop.Contains("ubuntu", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP"), "ubuntu", StringComparison.OrdinalIgnoreCase));
    }

    public void ConfigureDesktopClipboard(
        Func<CancellationToken, Task<string?>> textReader,
        Func<string, CancellationToken, Task> textWriter)
    {
        ArgumentNullException.ThrowIfNull(textReader);
        ArgumentNullException.ThrowIfNull(textWriter);
        _desktopTextReader = textReader;
        _desktopTextWriter = textWriter;
    }

    private async Task<string?> ReadNativeTextAsync(CancellationToken ct)
    {
        // Avalonia's X11 clipboard shares asynchronous selection-request state.
        // Serialize polling, explicit reads and writes so they cannot consume each other's replies.
        await _nativeClipboardAccess.WaitAsync(ct).ConfigureAwait(false);
        try { return await _desktopTextReader!(ct).ConfigureAwait(false); }
        finally { _nativeClipboardAccess.Release(); }
    }

    public async Task<string?> GetTextAsync(CancellationToken ct = default)
    {
        if (_desktopTextReader != null)
        {
            try { return await ReadNativeTextAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* Try the command backend if native clipboard is unavailable. */ }
        }

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
        if (_desktopTextWriter != null)
        {
            await _nativeClipboardAccess.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _desktopTextWriter(text, ct).ConfigureAwait(false);
                _lastClipboardText = text;
            }
            finally { _nativeClipboardAccess.Release(); }
            return;
        }

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

        // Primary Selection is sampled by the global mouse hook. Never invoke the
        // focus-stealing wl-paste fallback on every click in GNOME Wayland.
        if (_hasWaylandDisplay && !_isGnomeWayland)
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
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _lastClipboardText = null;
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
        // Native access works even without xclip. Without it, use only the silent X11
        // backend here: wl-paste's GNOME fallback can steal focus during polling.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string? currentClip = _desktopTextReader != null
                    ? await ReadNativeTextAsync(ct).ConfigureAwait(false)
                    : _hasX11Display
                        ? await RunReadCommandAsync("xclip", ["-o", "-selection", "clipboard"], ct).ConfigureAwait(false)
                        : null;
                ct.ThrowIfCancellationRequested();
                if (currentClip != null && !string.Equals(currentClip, _lastClipboardText, StringComparison.Ordinal))
                {
                    _lastClipboardText = currentClip;
                    // Capture the clipboard already present at startup, not just subsequent copies.
                    if (!string.IsNullOrWhiteSpace(currentClip)) ClipboardTextChanged?.Invoke(this, currentClip);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { /* Retry after transient clipboard ownership changes. */ }

            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static async Task<string?> RunReadCommandAsync(string fileName, string[] args, CancellationToken ct)
    {
        Process? process = null;
        try
        {
            process = new Process
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

            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            var output = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Ignore
                }
                process.Dispose();
            }
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
                    RedirectStandardError = false,
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

            return !ct.IsCancellationRequested && (!process.HasExited || process.ExitCode == 0);
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
