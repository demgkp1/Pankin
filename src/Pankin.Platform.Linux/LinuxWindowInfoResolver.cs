using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pankin.Platform.Linux;

public sealed record LinuxActiveWindowInfo(string? AppName, string? Title);

/// <summary>
/// 读取 Linux (X11 / XWayland) 当前活跃前台窗口的应用名称与窗口标题。
/// 采用非阻塞短超时 (120ms) 执行 xprop，失败或超时则优雅降级返回 null，保证绝对不卡死前台。
/// </summary>
public static class LinuxWindowInfoResolver
{
    public static async Task<LinuxActiveWindowInfo?> GetActiveWindowInfoAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return null;
        }

        Process? proc1 = null;
        Process? proc2 = null;

        try
        {
            // 1. 查询当前前台活跃窗口 ID
            proc1 = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "xprop",
                    ArgumentList = { "-root", "_NET_ACTIVE_WINDOW" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc1.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(120);

            var output = await proc1.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await proc1.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            var hashIdx = output.IndexOf('#');
            if (hashIdx < 0) return null;

            var winId = output[(hashIdx + 1)..].Trim();
            if (string.IsNullOrEmpty(winId) || winId == "0x0") return null;

            // 2. 根据窗口 ID 查询 WM_CLASS 与 _NET_WM_NAME
            proc2 = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "xprop",
                    ArgumentList = { "-id", winId, "WM_CLASS", "_NET_WM_NAME" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc2.Start();

            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(120);

            var winProps = await proc2.StandardOutput.ReadToEndAsync(cts2.Token).ConfigureAwait(false);
            await proc2.WaitForExitAsync(cts2.Token).ConfigureAwait(false);

            string? appName = null;
            string? title = null;

            foreach (var line in winProps.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("WM_CLASS") && trimmed.Contains('='))
                {
                    var val = trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
                    var tokens = val.Split(new[] { ',', '"', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (tokens.Length > 0)
                    {
                        appName = string.Join(" ", tokens.Distinct(StringComparer.OrdinalIgnoreCase));
                    }
                }
                else if (trimmed.StartsWith("_NET_WM_NAME") && trimmed.Contains('='))
                {
                    var val = trimmed[(trimmed.IndexOf('=') + 1)..].Trim().Trim('"');
                    title = val;
                }
            }

            return new LinuxActiveWindowInfo(appName, title);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (proc1 != null)
            {
                try { if (!proc1.HasExited) proc1.Kill(entireProcessTree: true); } catch { }
                proc1.Dispose();
            }
            if (proc2 != null)
            {
                try { if (!proc2.HasExited) proc2.Kill(entireProcessTree: true); } catch { }
                proc2.Dispose();
            }
        }
    }
}
