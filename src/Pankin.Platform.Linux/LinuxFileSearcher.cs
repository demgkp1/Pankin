using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Pankin.Core.Abstractions;
using Pankin.Core.Launcher;

namespace Pankin.Platform.Linux;

/// <summary>
/// 基于“用户主目录 BFS 广度优先实时索引（含最近文件与汉字按需拼音匹配）+ plocate -b 全盘检索 + find 实时兜底”的本地文件流式检索实现
/// </summary>
public sealed class LinuxFileSearcher : IFileSearcher, IDisposable
{
    private sealed record IndexedEntry(string FullPath, string FileName, string DirectoryPath, bool IsDirectory, bool HasChinese);

    private static readonly HashSet<string> ExcludedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "target", "dist", "build", "__pycache__", "venv", ".venv",
        ".cache", ".git", ".hg", ".svn", ".Trash", "Trash",
        ".cargo", ".rustup", ".npm", ".nvm", ".pnpm-store", ".yarn",
        ".nuget", ".dotnet", ".gradle", ".m2", ".ivy2", ".sbt",
        ".pyenv", ".virtualenvs", ".conda", ".mozilla", ".thunderbird", ".pki",
        ".steam", ".wine", ".vscode-server", ".cursor-server", "site-packages"
    };

    private static readonly string[] PriorityDirNames =
    [
        ".gemini", ".claude", ".cursor", "Desktop", "Downloads", "Documents", "projects", "workspace", "code", "app", ".config"
    ];

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SearchTriggerThreshold = TimeSpan.FromSeconds(30);
    private const int MaxCacheEntries = 150000;
    private const int MaxScanDepth = 7;

    private readonly string? _userHome;
    private readonly Timer? _refreshTimer;
    private readonly TaskCompletionSource _firstScanTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile List<IndexedEntry> _cachedEntries = new();
    private long _lastScanTicks;
    private int _isScanning;

    public LinuxFileSearcher()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home))
        {
            _userHome = home;
            _refreshTimer = new Timer(_ => TriggerBackgroundScan(), null, RefreshInterval, RefreshInterval);
            TriggerBackgroundScan();
        }
        else
        {
            _firstScanTcs.TrySetResult();
        }
    }

    private static bool ContainsChinese(string text)
    {
        foreach (char c in text)
        {
            if (c >= '\u4e00' && c <= '\u9fff') return true;
        }
        return false;
    }

    private void TriggerBackgroundScan()
    {
        if (string.IsNullOrEmpty(_userHome))
        {
            _firstScanTcs.TrySetResult();
            return;
        }

        if (Interlocked.CompareExchange(ref _isScanning, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var newEntries = new List<IndexedEntry>(Math.Min(_cachedEntries.Count + 1000, MaxCacheEntries));
                ScanUserHome(_userHome, newEntries);
                _cachedEntries = newEntries;
                Volatile.Write(ref _lastScanTicks, DateTime.UtcNow.Ticks);
            }
            catch
            {
                // 忽略后台扫描异常，保障主程序稳定
            }
            finally
            {
                _firstScanTcs.TrySetResult();
                Interlocked.Exchange(ref _isScanning, 0);
            }
        });
    }

    private static bool IsIgnoredDirectory(string fullPath, string dirName)
    {
        if (ExcludedDirNames.Contains(dirName))
        {
            return true;
        }

        if (fullPath.Contains("/.local/", StringComparison.OrdinalIgnoreCase))
        {
            if (fullPath.Contains("/.local/share/Trash", StringComparison.OrdinalIgnoreCase) ||
                fullPath.Contains("/.local/share/Steam", StringComparison.OrdinalIgnoreCase) ||
                fullPath.Contains("/.local/share/containers", StringComparison.OrdinalIgnoreCase) ||
                fullPath.Contains("/.local/share/flatpak", StringComparison.OrdinalIgnoreCase) ||
                fullPath.Contains("/.local/lib", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void ScanUserHome(string rootDir, List<IndexedEntry> entries)
    {
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        // 1. 优先解析 Linux 最近使用文件数据库 (~/.local/share/recently-used.xbel)
        var xbelPath = Path.Combine(rootDir, ".local", "share", "recently-used.xbel");
        if (File.Exists(xbelPath))
        {
            try
            {
                var content = File.ReadAllText(xbelPath);
                var matches = Regex.Matches(content, @"href=""file://([^""]+)""", RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                {
                    if (entries.Count >= MaxCacheEntries) break;
                    if (!match.Success) continue;

                    var rawPath = match.Groups[1].Value;
                    string fullPath;
                    try
                    {
                        fullPath = Uri.UnescapeDataString(rawPath);
                        if (!fullPath.StartsWith('/'))
                        {
                            fullPath = "/" + fullPath;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    if (!seenPaths.Add(fullPath)) continue;

                    bool isDir = Directory.Exists(fullPath);
                    bool isFile = !isDir && File.Exists(fullPath);
                    if (isDir || isFile)
                    {
                        var fileName = Path.GetFileName(fullPath);
                        if (string.IsNullOrEmpty(fileName)) fileName = fullPath;
                        var dirPath = Path.GetDirectoryName(fullPath) ?? "/";
                        entries.Add(new IndexedEntry(fullPath, fileName, dirPath, isDir, ContainsChinese(fileName)));
                    }
                }
            }
            catch
            {
                // 忽略 xbel 解析异常
            }
        }

        // 2. 扫描 rootDir 下的所有直接文件
        try
        {
            var rootFiles = Directory.GetFiles(rootDir);
            foreach (var file in rootFiles)
            {
                if (entries.Count >= MaxCacheEntries) return;
                if (!seenPaths.Add(file)) continue;

                var fileName = Path.GetFileName(file);
                entries.Add(new IndexedEntry(file, fileName, rootDir, false, ContainsChinese(fileName)));
            }
        }
        catch
        {
        }

        // 3. 构建 BFS 队列，PriorityDirNames 优先入队
        var queue = new Queue<(string DirPath, int Depth)>();
        var queuedDirs = new HashSet<string>(StringComparer.Ordinal);

        // 3.1 优先目录入队
        foreach (var pName in PriorityDirNames)
        {
            var pPath = Path.Combine(rootDir, pName);
            if (Directory.Exists(pPath) && queuedDirs.Add(pPath))
            {
                if (seenPaths.Add(pPath))
                {
                    entries.Add(new IndexedEntry(pPath, pName, rootDir, true, ContainsChinese(pName)));
                }
                queue.Enqueue((pPath, 1));
            }
        }

        // 3.2 其它非黑名单一级目录入队
        try
        {
            var topDirs = Directory.GetDirectories(rootDir);
            foreach (var dir in topDirs)
            {
                var dirName = Path.GetFileName(dir);
                if (IsIgnoredDirectory(dir, dirName)) continue;
                if (queuedDirs.Add(dir))
                {
                    if (seenPaths.Add(dir))
                    {
                        entries.Add(new IndexedEntry(dir, dirName, rootDir, true, ContainsChinese(dirName)));
                    }
                    queue.Enqueue((dir, 1));
                }
            }
        }
        catch
        {
        }

        // 4. BFS 逐层出队扫描（保证浅层工作区与高频目录 100% 被收录）
        while (queue.Count > 0 && entries.Count < MaxCacheEntries)
        {
            var (currentDir, currentDepth) = queue.Dequeue();

            // 4.1 先将该目录下的所有文件加入 entries
            try
            {
                var files = Directory.GetFiles(currentDir);
                foreach (var file in files)
                {
                    if (entries.Count >= MaxCacheEntries) return;
                    if (!seenPaths.Add(file)) continue;

                    var fileName = Path.GetFileName(file);
                    entries.Add(new IndexedEntry(file, fileName, currentDir, false, ContainsChinese(fileName)));
                }
            }
            catch
            {
                // 忽略文件读取错误
            }

            // 4.2 若未达最大深度，再将子目录加入 entries 并入队
            if (currentDepth < MaxScanDepth)
            {
                try
                {
                    var subDirs = Directory.GetDirectories(currentDir);
                    foreach (var subDir in subDirs)
                    {
                        if (entries.Count >= MaxCacheEntries) return;

                        var dirName = Path.GetFileName(subDir);
                        if (IsIgnoredDirectory(subDir, dirName)) continue;

                        if (seenPaths.Add(subDir))
                        {
                            entries.Add(new IndexedEntry(subDir, dirName, currentDir, true, ContainsChinese(dirName)));
                        }

                        queue.Enqueue((subDir, currentDepth + 1));
                    }
                }
                catch
                {
                    // 忽略目录枚举错误
                }
            }
        }
    }

    public async IAsyncEnumerable<FileSearchResult> SearchFilesAsync(
        string query,
        int limit = 50,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            yield break;
        }

        var trimmed = query.Trim();
        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            yield break;
        }

        var effectiveLimit = Math.Clamp(limit, 1, 500);

        // 若首次扫描尚未完成且缓存为空，等待最多 300ms
        if (_cachedEntries.Count == 0 && !_firstScanTcs.Task.IsCompleted)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(300);
                await _firstScanTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // 超时或取消后直接继续，不阻断流程
            }
        }

        // 若距上次扫描超过阈值，异步唤醒更新
        var lastTicks = Volatile.Read(ref _lastScanTicks);
        if (lastTicks == 0 || DateTime.UtcNow.Ticks - lastTicks > SearchTriggerThreshold.Ticks)
        {
            TriggerBackgroundScan();
        }

        var returnedPaths = new HashSet<string>(StringComparer.Ordinal);
        int yieldedCount = 0;

        // 1. 主目录实时索引高速匹配（含多词交集，汉字按需拼音匹配，纯英文零额外开销）
        var cached = _cachedEntries;
        if (cached.Count > 0)
        {
            foreach (var entry in cached)
            {
                ct.ThrowIfCancellationRequested();

                bool matchesAll = true;
                foreach (var token in tokens)
                {
                    bool matchToken;
                    if (token.Contains('/'))
                    {
                        matchToken = entry.FullPath.Contains(token, StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        matchToken = entry.FileName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                                     (entry.HasChinese && PinyinMatcher.Instance.Match(entry.FileName, token) != null);
                    }

                    if (!matchToken)
                    {
                        matchesAll = false;
                        break;
                    }
                }

                if (matchesAll && returnedPaths.Add(entry.FullPath))
                {
                    yieldedCount++;
                    yield return new FileSearchResult(entry.FullPath, entry.FileName, entry.DirectoryPath, entry.IsDirectory);

                    if (yieldedCount >= effectiveLimit)
                    {
                        yield break;
                    }
                }
            }
        }

        // 2. 若主目录结果不足 limit，调用 plocate 全盘检索补齐
        if (yieldedCount < effectiveLimit)
        {
            var plocateLimit = Math.Clamp(effectiveLimit * 8, 150, 400);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "plocate",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add("-l");
            process.StartInfo.ArgumentList.Add(plocateLimit.ToString());

            if (!trimmed.Contains('/'))
            {
                process.StartInfo.ArgumentList.Add("-b");
            }

            process.StartInfo.ArgumentList.Add("--");
            foreach (var token in tokens)
            {
                process.StartInfo.ArgumentList.Add(token);
            }

            bool started;
            try
            {
                started = process.Start();
            }
            catch
            {
                yield break;
            }

            if (!started)
            {
                yield break;
            }

            using var registration = ct.Register(() =>
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
                    // Ignore race condition on process exit
                }
            });

            var reader = process.StandardOutput;

            while (!ct.IsCancellationRequested && yieldedCount < effectiveLimit)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                catch
                {
                    yield break;
                }

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var fullPath = line.Trim();

                // 过滤虚拟文件系统路径
                if (fullPath.StartsWith("/proc/") ||
                    fullPath.StartsWith("/sys/") ||
                    fullPath.StartsWith("/dev/") ||
                    fullPath.StartsWith("/run/"))
                {
                    continue;
                }

                if (!returnedPaths.Add(fullPath))
                {
                    continue;
                }

                var fileName = Path.GetFileName(fullPath);
                if (string.IsNullOrEmpty(fileName))
                {
                    fileName = fullPath;
                }

                var dirPath = Path.GetDirectoryName(fullPath) ?? "/";
                var isDir = Directory.Exists(fullPath);

                yieldedCount++;
                yield return new FileSearchResult(fullPath, fileName, dirPath, isDir);
            }

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
        }

        // 3. 当主目录缓存与 plocate 均未命中时，启动 find 实时文件系统查找兜底
        if (yieldedCount == 0 && !string.IsNullOrEmpty(_userHome))
        {
            var firstToken = tokens[0];
            using var findProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "find",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            findProcess.StartInfo.ArgumentList.Add(_userHome);
            findProcess.StartInfo.ArgumentList.Add("-maxdepth");
            findProcess.StartInfo.ArgumentList.Add("7");
            findProcess.StartInfo.ArgumentList.Add("(");
            findProcess.StartInfo.ArgumentList.Add("-name");
            findProcess.StartInfo.ArgumentList.Add(".cache");
            findProcess.StartInfo.ArgumentList.Add("-o");
            findProcess.StartInfo.ArgumentList.Add("-name");
            findProcess.StartInfo.ArgumentList.Add(".git");
            findProcess.StartInfo.ArgumentList.Add("-o");
            findProcess.StartInfo.ArgumentList.Add("-name");
            findProcess.StartInfo.ArgumentList.Add("node_modules");
            findProcess.StartInfo.ArgumentList.Add("-o");
            findProcess.StartInfo.ArgumentList.Add("-name");
            findProcess.StartInfo.ArgumentList.Add("bin");
            findProcess.StartInfo.ArgumentList.Add("-o");
            findProcess.StartInfo.ArgumentList.Add("-name");
            findProcess.StartInfo.ArgumentList.Add("obj");
            findProcess.StartInfo.ArgumentList.Add("-o");
            findProcess.StartInfo.ArgumentList.Add("-name");
            findProcess.StartInfo.ArgumentList.Add(".cargo");
            findProcess.StartInfo.ArgumentList.Add("-o");
            findProcess.StartInfo.ArgumentList.Add("-name");
            findProcess.StartInfo.ArgumentList.Add(".rustup");
            findProcess.StartInfo.ArgumentList.Add("-o");
            findProcess.StartInfo.ArgumentList.Add("-name");
            findProcess.StartInfo.ArgumentList.Add(".npm");
            findProcess.StartInfo.ArgumentList.Add("-o");
            findProcess.StartInfo.ArgumentList.Add("-name");
            findProcess.StartInfo.ArgumentList.Add(".nuget");
            findProcess.StartInfo.ArgumentList.Add("-o");
            findProcess.StartInfo.ArgumentList.Add("-name");
            findProcess.StartInfo.ArgumentList.Add(".dotnet");
            findProcess.StartInfo.ArgumentList.Add("-o");
            findProcess.StartInfo.ArgumentList.Add("-name");
            findProcess.StartInfo.ArgumentList.Add(".mozilla");
            findProcess.StartInfo.ArgumentList.Add("-o");
            findProcess.StartInfo.ArgumentList.Add("-name");
            findProcess.StartInfo.ArgumentList.Add(".local");
            findProcess.StartInfo.ArgumentList.Add(")");
            findProcess.StartInfo.ArgumentList.Add("-prune");
            findProcess.StartInfo.ArgumentList.Add("-o");
            findProcess.StartInfo.ArgumentList.Add("-iname");
            findProcess.StartInfo.ArgumentList.Add($"*{firstToken}*");
            findProcess.StartInfo.ArgumentList.Add("-print");

            bool findStarted;
            try
            {
                findStarted = findProcess.Start();
            }
            catch
            {
                findStarted = false;
            }

            if (findStarted)
            {
                using var registration = ct.Register(() =>
                {
                    try
                    {
                        if (!findProcess.HasExited)
                        {
                            findProcess.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                    }
                });

                var reader = findProcess.StandardOutput;

                while (!ct.IsCancellationRequested && yieldedCount < effectiveLimit)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        yield break;
                    }
                    catch
                    {
                        yield break;
                    }

                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var fullPath = line.Trim();
                    if (!returnedPaths.Add(fullPath))
                    {
                        continue;
                    }

                    var fileName = Path.GetFileName(fullPath);
                    if (string.IsNullOrEmpty(fileName))
                    {
                        fileName = fullPath;
                    }

                    // 验证是否满足全部 tokens
                    bool matchesAll = true;
                    foreach (var token in tokens)
                    {
                        bool matchToken = token.Contains('/')
                            ? fullPath.Contains(token, StringComparison.OrdinalIgnoreCase)
                            : (fileName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                               (ContainsChinese(fileName) && PinyinMatcher.Instance.Match(fileName, token) != null));

                        if (!matchToken)
                        {
                            matchesAll = false;
                            break;
                        }
                    }

                    if (matchesAll)
                    {
                        var dirPath = Path.GetDirectoryName(fullPath) ?? "/";
                        var isDir = Directory.Exists(fullPath);
                        yieldedCount++;
                        yield return new FileSearchResult(fullPath, fileName, dirPath, isDir);
                    }
                }

                try
                {
                    if (!findProcess.HasExited)
                    {
                        findProcess.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }
            }
        }
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
    }
}
