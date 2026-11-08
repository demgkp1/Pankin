using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Pankin.Core.Setup;

/// <summary>
/// Linux 平台环境依赖检测器。
/// 扫描 plocate、wl-clipboard、xclip 与 libayatana-appindicator 的安装就绪情况。
/// </summary>
public sealed class LinuxDependencyChecker : IDependencyChecker
{
    private readonly Func<string, string?>? _executableResolver;
    private readonly Func<string, bool>? _libraryResolver;

    public LinuxDependencyChecker(Func<string, string?>? executableResolver = null, Func<string, bool>? libraryResolver = null)
    {
        _executableResolver = executableResolver;
        _libraryResolver = libraryResolver;
    }

    public Task<IReadOnlyList<LinuxDependencyStatus>> CheckDependenciesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var list = new List<LinuxDependencyStatus>();

        // 1. plocate 检索器
        var plocatePath = FindExecutable("plocate");
        list.Add(new LinuxDependencyStatus(
            Name: "plocate",
            Description: "本地文件毫秒级检索后端",
            IsInstalled: plocatePath != null,
            IsRecommended: true,
            InstallCommand: "sudo apt install plocate",
            DetectedPath: plocatePath));

        // 2. wl-clipboard (Wayland 剪贴板)
        var wlCopyPath = FindExecutable("wl-copy");
        list.Add(new LinuxDependencyStatus(
            Name: "wl-clipboard",
            Description: "Wayland 桌面环境剪贴板与主选区支持",
            IsInstalled: wlCopyPath != null,
            IsRecommended: true,
            InstallCommand: "sudo apt install wl-clipboard",
            DetectedPath: wlCopyPath));

        // 3. xclip (X11 剪贴板)
        var xclipPath = FindExecutable("xclip");
        list.Add(new LinuxDependencyStatus(
            Name: "xclip",
            Description: "X11 / XWayland 桌面环境剪贴板与主选区支持",
            IsInstalled: xclipPath != null,
            IsRecommended: true,
            InstallCommand: "sudo apt install xclip",
            DetectedPath: xclipPath));

        // 4. libayatana-appindicator (托盘图标支持)
        var hasAyatana = CheckLibrary("libayatana-appindicator3");
        list.Add(new LinuxDependencyStatus(
            Name: "libayatana-appindicator",
            Description: "Linux 桌面系统托盘图标支持",
            IsInstalled: hasAyatana,
            IsRecommended: false,
            InstallCommand: "sudo apt install libayatana-appindicator3-1",
            DetectedPath: hasAyatana ? "/usr/lib/x86_64-linux-gnu/libayatana-appindicator3.so.1" : null));

        return Task.FromResult<IReadOnlyList<LinuxDependencyStatus>>(list);
    }

    private string? FindExecutable(string name)
    {
        if (_executableResolver != null)
        {
            return _executableResolver(name);
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin:/usr/local/bin";
        foreach (var dir in pathEnv.Split(":", StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir, name);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    private bool CheckLibrary(string libNamePrefix)
    {
        if (_libraryResolver != null)
        {
            return _libraryResolver(libNamePrefix);
        }

        var standardDirs = new[]
        {
            "/usr/lib/x86_64-linux-gnu",
            "/usr/lib64",
            "/usr/lib",
            "/lib/x86_64-linux-gnu",
            "/lib64",
            "/lib"
        };

        foreach (var dir in standardDirs)
        {
            if (Directory.Exists(dir))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, libNamePrefix + "*.so*"))
                    {
                        return true;
                    }
                }
                catch
                {
                    // 权限受限跳过
                }
            }
        }

        return false;
    }
}
