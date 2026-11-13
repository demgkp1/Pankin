using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Pankin.Core.Abstractions;
using Pankin.Core.Launcher;

namespace Pankin.Platform.Linux;

/// <summary>
/// 基于 Ubuntu 24.04 及 Linux XDG 规范的应用扫描与启动实现
/// </summary>
public sealed partial class LinuxAppScanner : IAppScanner
{
    private readonly string[] _desktopDirectories;
    private readonly string[] _appImageDirectories;

    public LinuxAppScanner(IEnumerable<string>? desktopDirectories = null, IEnumerable<string>? appImageDirectories = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome)) dataHome = Path.Combine(home, ".local", "share");
        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrWhiteSpace(dataDirs)) dataDirs = "/usr/local/share:/usr/share";
        _desktopDirectories = (desktopDirectories ?? new[]
        {
            Path.Combine(dataHome, "applications"),
            Path.Combine(home, ".local", "share", "flatpak", "exports", "share", "applications"),
            "/var/lib/flatpak/exports/share/applications", "/var/lib/snapd/desktop/applications"
        }.Concat(dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d, "applications"))))
            .Distinct(StringComparer.Ordinal).ToArray();
        _appImageDirectories = (appImageDirectories ?? new[]
        {
            Path.Combine(home, "Applications"), Path.Combine(home, "AppImages"),
            Path.Combine(home, "app"), Path.Combine(home, "Downloads"), Path.Combine(home, "下载"),
            Path.Combine(home, ".local", "bin"), GetDownloadsDirectory(home)
        }).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string GetDownloadsDirectory(string home)
    {
        try
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(config)) config = Path.Combine(home, ".config");
            var line = File.ReadLines(Path.Combine(config, "user-dirs.dirs"))
                .FirstOrDefault(l => l.StartsWith("XDG_DOWNLOAD_DIR=", StringComparison.Ordinal));
            var path = line?.Split('=', 2)[1].Trim().Trim('"').Replace("$HOME", home);
            if (!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path)) return path;
        }
        catch { }
        return Path.Combine(home, "Downloads");
    }

    [GeneratedRegex(@"%[fFuUdDnNickvm]")]
    private static partial Regex ExecPlaceholderRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleWhitespaceRegex();

    private readonly Lazy<LinuxIconResolver> _icons = new(() => new LinuxIconResolver());

    public byte[]? LoadIconPng(string iconName, int pixels = 96) => _icons.Value.LoadPng(iconName, pixels);

    private readonly object _lock = new();
    private IReadOnlyList<AppEntry> _cachedApps = Array.Empty<AppEntry>();

    public async Task<IReadOnlyList<AppEntry>> ScanAppsAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<AppEntry>();

            foreach (var directory in _desktopDirectories)
            {
                if (ct.IsCancellationRequested) break;
                if (!Directory.Exists(directory)) continue;

                try
                {
                    var files = Directory.GetFiles(directory, "*.desktop", SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        if (ct.IsCancellationRequested) break;

                        var appId = Path.GetFileNameWithoutExtension(file);
                        if (!seenIds.Add(appId))
                        {
                            // 用户目录优先，已出现同名 desktop 则跳过系统或底层同名文件
                            continue;
                        }

                        var entry = ParseDesktopFile(file, appId);
                        if (entry != null)
                        {
                            results.Add(entry);
                        }
                    }
                }
                catch
                {
                    // 忽略无权限或受损目录
                }
            }

            // AppImages do not necessarily install a desktop entry. Discover portable
            // executables in common user application/download folders without scanning all of HOME.
            var seenPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var app in results)
            {
                var match = Regex.Match(app.Exec, "(?:^|\\s)(?:\"(?<path>[^\"]+\\.appimage)\"|'(?<path>[^']+\\.appimage)'|(?<path>[^\\s]+\\.appimage))(?=\\s|$)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    try { seenPaths.Add(CanonicalPath(match.Groups["path"].Value)); }
                    catch (IOException) { /* Stale desktop entry; continue scanning portable apps. */ }
                    catch (UnauthorizedAccessException) { }
                }
            }
            foreach (var directory in _appImageDirectories)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(directory)) continue;
                try
                {
                    var options = new EnumerationOptions
                    {
                        RecurseSubdirectories = true, MaxRecursionDepth = 2,
                        IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
                    };
                    foreach (var file in Directory.EnumerateFiles(directory, "*", options))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!Path.GetExtension(file).Equals(".AppImage", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            var path = CanonicalPath(file);
                            const UnixFileMode executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                            if (!OperatingSystem.IsLinux() || (File.GetUnixFileMode(path) & executable) == 0 || !seenPaths.Add(path)) continue;
                            var fileName = Path.GetFileNameWithoutExtension(path);
                            var name = Regex.Replace(fileName, @"[-_]v?\d.*$", "", RegexOptions.IgnoreCase).Replace('_', ' ');
                            if (string.IsNullOrWhiteSpace(name)) name = fileName;
                            var icon = new[] { Path.ChangeExtension(path, ".png"), Path.ChangeExtension(path, ".svg") }.FirstOrDefault(File.Exists)
                                ?? "application-x-executable";
                            var id = "appimage:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..16].ToLowerInvariant();
                            results.Add(new AppEntry(id, name, null, "\"" + path + "\"", icon, path, Path.GetDirectoryName(path), false));
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            var sorted = results
                .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            lock (_lock)
            {
                _cachedApps = sorted;
            }

            return (IReadOnlyList<AppEntry>)sorted;
        }, ct).ConfigureAwait(false);
    }

    private static string CanonicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return new FileInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fullPath;
    }

    public IReadOnlyList<AppEntry> SearchApps(string query, int maxResults = 20)
    {
        IReadOnlyList<AppEntry> currentList;
        lock (_lock)
        {
            currentList = _cachedApps;
        }

        var limit = Math.Clamp(maxResults, 1, 100);

        if (string.IsNullOrWhiteSpace(query))
        {
            return currentList.Take(limit).ToList();
        }

        var trimmed = query.Trim();
        var matcher = PinyinMatcher.Instance;
        var scoredList = new List<(AppEntry App, int Score)>();

        foreach (var app in currentList)
        {
            int maxScore = 0;

            // 1. 匹配本地化显示名称
            var m1 = matcher.Match(app.DisplayName, trimmed);
            if (m1 != null && m1.Score > maxScore) maxScore = m1.Score;

            // 2. 匹配原始英文名称
            if (!string.Equals(app.DisplayName, app.Name, StringComparison.OrdinalIgnoreCase))
            {
                var m2 = matcher.Match(app.Name, trimmed);
                if (m2 != null && m2.Score > maxScore) maxScore = m2.Score;
            }

            // 3. 匹配应用 ID / 文件名
            var m3 = matcher.Match(app.Id, trimmed);
            if (m3 != null && m3.Score > maxScore) maxScore = m3.Score;

            if (maxScore > 0)
            {
                scoredList.Add((app, maxScore));
            }
        }

        return scoredList
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.App.DisplayName.Length)
            .ThenBy(x => x.App.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.App)
            .Take(limit)
            .ToList();
    }

    public async Task<bool> LaunchAppAsync(AppEntry app, CancellationToken ct = default)
    {
        if (app == null || string.IsNullOrWhiteSpace(app.Exec))
            return false;

        return await Task.Run(() =>
        {
            try
            {
                // Discovered AppImages are executed directly, never interpreted by a shell.
                if (Path.GetExtension(app.DesktopFilePath).Equals(".AppImage", StringComparison.OrdinalIgnoreCase))
                {
                    using var appImageProcess = Process.Start(new ProcessStartInfo
                    {
                        FileName = app.DesktopFilePath, UseShellExecute = false,
                        WorkingDirectory = app.WorkingDirectory ?? Path.GetDirectoryName(app.DesktopFilePath)!
                    });
                    return appImageProcess != null;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                string execCommand = app.Exec;
                if (app.Terminal)
                {
                    execCommand = $"x-terminal-emulator -e {execCommand} || gnome-terminal -- {execCommand} || {execCommand}";
                }

                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(execCommand);

                if (!string.IsNullOrWhiteSpace(app.WorkingDirectory) && Directory.Exists(app.WorkingDirectory))
                {
                    psi.WorkingDirectory = app.WorkingDirectory;
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

    private static AppEntry? ParseDesktopFile(string filePath, string appId)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new StreamReader(stream);

            bool inDesktopEntry = false;
            string? name = null;
            string? localizedName = null;
            string? exec = null;
            string? icon = null;
            string? type = null;
            string? workingDir = null;
            bool noDisplay = false;
            bool hidden = false;
            bool terminal = false;

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                    continue;

                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    if (string.Equals(trimmed, "[Desktop Entry]", StringComparison.OrdinalIgnoreCase))
                    {
                        inDesktopEntry = true;
                    }
                    else
                    {
                        // 遇到下一个节（如 [Desktop Action ...]），停止继续覆盖主属性
                        break;
                    }
                    continue;
                }

                if (!inDesktopEntry) continue;

                var eqIndex = trimmed.IndexOf('=');
                if (eqIndex <= 0) continue;

                var key = trimmed[..eqIndex].Trim();
                var value = trimmed[(eqIndex + 1)..].Trim();

                if (key.Equals("Type", StringComparison.OrdinalIgnoreCase))
                {
                    type = value;
                }
                else if (key.Equals("NoDisplay", StringComparison.OrdinalIgnoreCase))
                {
                    noDisplay = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                }
                else if (key.Equals("Hidden", StringComparison.OrdinalIgnoreCase))
                {
                    hidden = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                }
                else if (key.Equals("Name", StringComparison.Ordinal))
                {
                    name ??= value;
                }
                else if (key.Equals("Name[zh_CN]", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("Name[zh_Hans]", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("Name[zh]", StringComparison.OrdinalIgnoreCase))
                {
                    localizedName ??= value;
                }
                else if (key.Equals("Exec", StringComparison.OrdinalIgnoreCase))
                {
                    exec ??= CleanExec(value);
                }
                else if (key.Equals("Icon", StringComparison.OrdinalIgnoreCase))
                {
                    icon ??= value;
                }
                else if (key.Equals("Path", StringComparison.OrdinalIgnoreCase))
                {
                    workingDir ??= value;
                }
                else if (key.Equals("Terminal", StringComparison.OrdinalIgnoreCase))
                {
                    terminal = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                }
            }

            // 过滤无效条目
            if (noDisplay || hidden) return null;
            if (!string.Equals(type, "Application", StringComparison.OrdinalIgnoreCase)) return null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(exec)) return null;

            return new AppEntry(
                Id: appId,
                Name: name,
                LocalizedName: localizedName,
                Exec: exec,
                Icon: icon,
                DesktopFilePath: filePath,
                WorkingDirectory: workingDir,
                Terminal: terminal);
        }
        catch
        {
            return null;
        }
    }

    private static string CleanExec(string rawExec)
    {
        var withoutCodes = ExecPlaceholderRegex().Replace(rawExec, "");
        return MultipleWhitespaceRegex().Replace(withoutCodes, " ").Trim();
    }
}
