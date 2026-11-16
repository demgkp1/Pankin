using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pankin.Platform.Linux;

public sealed record LinuxActiveWindowInfo(string? AppName, string? Title);

/// <summary>
/// 读取 Linux (X11 / XWayland 及 Niri / Sway / Hyprland Wayland) 当前活跃前台窗口的应用名称与窗口标题。
/// 采用非阻塞短超时 (120ms) 执行外部查询，失败或超时则优雅降级返回 null，保证绝对不卡死前台。
/// </summary>
public static class LinuxWindowInfoResolver
{
    private const int CommandTimeoutMs = 120;

    public static async Task<LinuxActiveWindowInfo?> GetActiveWindowInfoAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NIRI_SOCKET")))
        {
            var niriInfo = await TryGetNiriActiveWindowAsync(ct).ConfigureAwait(false);
            if (niriInfo != null)
            {
                return niriInfo;
            }
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWAYSOCK")))
        {
            var swayInfo = await TryGetSwayActiveWindowAsync(ct).ConfigureAwait(false);
            if (swayInfo != null)
            {
                return swayInfo;
            }
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE")))
        {
            var hyprInfo = await TryGetHyprlandActiveWindowAsync(ct).ConfigureAwait(false);
            if (hyprInfo != null)
            {
                return hyprInfo;
            }
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            var x11Info = await TryGetX11ActiveWindowAsync(ct).ConfigureAwait(false);
            if (x11Info != null)
            {
                return x11Info;
            }
        }

        return null;
    }

    public static LinuxActiveWindowInfo? ParseNiriFocusedWindow(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var appName = TryGetNonEmptyString(root, "app_id");
            var title = TryGetNonEmptyString(root, "title");

            return string.IsNullOrWhiteSpace(appName) && string.IsNullOrWhiteSpace(title)
                ? null
                : new LinuxActiveWindowInfo(appName, title);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<LinuxActiveWindowInfo?> TryGetNiriActiveWindowAsync(CancellationToken ct)
    {
        var json = await RunCommandWithTimeoutAsync("niri", ["msg", "-j", "focused-window"], CommandTimeoutMs, ct).ConfigureAwait(false);
        return ParseNiriFocusedWindow(json);
    }

    private static async Task<LinuxActiveWindowInfo?> TryGetX11ActiveWindowAsync(CancellationToken ct)
    {
        var output = await RunCommandWithTimeoutAsync("xprop", ["-root", "_NET_ACTIVE_WINDOW"], CommandTimeoutMs, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        var hashIdx = output.IndexOf('#');
        if (hashIdx < 0) return null;

        var winId = output[(hashIdx + 1)..].Trim();
        if (string.IsNullOrEmpty(winId) || winId == "0x0") return null;

        var winProps = await RunCommandWithTimeoutAsync("xprop", ["-id", winId, "WM_CLASS", "_NET_WM_NAME"], CommandTimeoutMs, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(winProps))
        {
            return null;
        }

        string? appName = null;
        string? title = null;

        foreach (var line in winProps.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("WM_CLASS", StringComparison.Ordinal) && trimmed.Contains('='))
            {
                var val = trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
                var tokens = val.Split(new[] { ',', '"', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length > 0)
                {
                    appName = string.Join(" ", tokens.Distinct(StringComparer.OrdinalIgnoreCase));
                }
            }
            else if (trimmed.StartsWith("_NET_WM_NAME", StringComparison.Ordinal) && trimmed.Contains('='))
            {
                var val = trimmed[(trimmed.IndexOf('=') + 1)..].Trim().Trim('"');
                title = val;
            }
        }

        return string.IsNullOrWhiteSpace(appName) && string.IsNullOrWhiteSpace(title)
            ? null
            : new LinuxActiveWindowInfo(appName, title);
    }

    private static async Task<LinuxActiveWindowInfo?> TryGetSwayActiveWindowAsync(CancellationToken ct)
    {
        var json = await RunCommandWithTimeoutAsync("swaymsg", ["-t", "get_tree"], CommandTimeoutMs, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var stack = new Stack<JsonElement>();
            stack.Push(doc.RootElement);

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node.ValueKind != JsonValueKind.Object) continue;

                if (node.TryGetProperty("focused", out var focusedProp) && focusedProp.ValueKind == JsonValueKind.True)
                {
                    var appName = TryGetNonEmptyString(node, "app_id");
                    string? title = TryGetNonEmptyString(node, "name");

                    if (node.TryGetProperty("window_properties", out var winProps) && winProps.ValueKind == JsonValueKind.Object)
                    {
                        appName ??= TryGetNonEmptyString(winProps, "class") ?? TryGetNonEmptyString(winProps, "instance");
                        title ??= TryGetNonEmptyString(winProps, "title");
                    }

                    return string.IsNullOrWhiteSpace(appName) && string.IsNullOrWhiteSpace(title)
                        ? null
                        : new LinuxActiveWindowInfo(appName, title);
                }

                PushChildArray(node, "nodes", stack);
                PushChildArray(node, "floating_nodes", stack);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static async Task<LinuxActiveWindowInfo?> TryGetHyprlandActiveWindowAsync(CancellationToken ct)
    {
        var json = await RunCommandWithTimeoutAsync("hyprctl", ["activewindow", "-j"], CommandTimeoutMs, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var appName = TryGetNonEmptyString(root, "class") ?? TryGetNonEmptyString(root, "initialClass");
            var title = TryGetNonEmptyString(root, "title") ?? TryGetNonEmptyString(root, "initialTitle");

            return string.IsNullOrWhiteSpace(appName) && string.IsNullOrWhiteSpace(title)
                ? null
                : new LinuxActiveWindowInfo(appName, title);
        }
        catch
        {
            return null;
        }
    }

    private static void PushChildArray(JsonElement node, string propertyName, Stack<JsonElement> stack)
    {
        if (node.TryGetProperty(propertyName, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in arr.EnumerateArray())
            {
                stack.Push(child);
            }
        }
    }

    private static string? TryGetNonEmptyString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var value = prop.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static async Task<string?> RunCommandWithTimeoutAsync(string fileName, string[] args, int timeoutMs, CancellationToken ct)
    {
        Process? proc = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            proc = new Process { StartInfo = startInfo };
            proc.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            var output = await proc.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return proc.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (proc != null)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                proc.Dispose();
            }
        }
    }
}
