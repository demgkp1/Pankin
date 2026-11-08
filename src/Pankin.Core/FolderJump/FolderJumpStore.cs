using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Pankin.Core.Launcher;

namespace Pankin.Core.FolderJump;

public sealed class FolderJumpItem
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("accessCount")]
    public int AccessCount { get; set; }

    [JsonPropertyName("lastAccessed")]
    public DateTimeOffset LastAccessed { get; set; }

    [JsonPropertyName("isPinned")]
    public bool IsPinned { get; set; }
}

/// <summary>
/// 跨平台文件夹历史与偏好 JSON 持久化存储库。
/// 提供高频、最近、固定与拼音模糊搜索能力。
/// </summary>
public sealed class FolderJumpStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, FolderJumpItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private bool _isLoaded;

    public FolderJumpStore(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            _filePath = customPath;
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dir = Path.Combine(home, ".config", "pankin");
            _filePath = Path.Combine(dir, "folder_jump.json");
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_isLoaded) return;

        try
        {
            if (File.Exists(_filePath))
            {
                var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
                var list = JsonSerializer.Deserialize<List<FolderJumpItem>>(json);
                if (list != null)
                {
                    _items.Clear();
                    foreach (var item in list)
                    {
                        if (!string.IsNullOrWhiteSpace(item.Path))
                        {
                            _items[Normalize(item.Path)] = item;
                        }
                    }
                }
            }
            else
            {
                PopulateDefaultDirectories();
            }
        }
        catch
        {
            PopulateDefaultDirectories();
        }
        finally
        {
            _isLoaded = true;
        }
    }

    private void PopulateDefaultDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return;

        var defaults = new[]
        {
            (home, "用户主目录 (Home)"),
            (Path.Combine(home, "Downloads"), "下载 (Downloads)"),
            (Path.Combine(home, "Documents"), "文档 (Documents)"),
            (Path.Combine(home, "Desktop"), "桌面 (Desktop)")
        };

        foreach (var (path, name) in defaults)
        {
            if (Directory.Exists(path))
            {
                _items[Normalize(path)] = new FolderJumpItem
                {
                    Path = path,
                    Name = name,
                    AccessCount = 1,
                    LastAccessed = DateTimeOffset.UtcNow,
                    IsPinned = false
                };
            }
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var list = _items.Values.ToList();
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_filePath, json, ct).ConfigureAwait(false);
        }
        catch
        {
            // 忽略写入磁盘瞬时异常，保证内存状态仍然有效
        }
    }

    public async Task RecordAccessAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);

            var norm = Normalize(path);
            var name = Path.GetFileName(norm);
            if (string.IsNullOrEmpty(name)) name = norm;

            if (_items.TryGetValue(norm, out var existing))
            {
                existing.AccessCount++;
                existing.LastAccessed = DateTimeOffset.UtcNow;
            }
            else
            {
                _items[norm] = new FolderJumpItem
                {
                    Path = norm,
                    Name = name,
                    AccessCount = 1,
                    LastAccessed = DateTimeOffset.UtcNow,
                    IsPinned = false
                };
            }

            await SaveAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TogglePinAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
            var norm = Normalize(path);
            if (_items.TryGetValue(norm, out var item))
            {
                item.IsPinned = !item.IsPinned;
                await SaveAsync(ct).ConfigureAwait(false);
            }
            else
            {
                var name = Path.GetFileName(norm);
                if (string.IsNullOrEmpty(name)) name = norm;

                _items[norm] = new FolderJumpItem
                {
                    Path = norm,
                    Name = name,
                    AccessCount = 1,
                    LastAccessed = DateTimeOffset.UtcNow,
                    IsPinned = true
                };
                await SaveAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<FolderJumpEntry>> GetEntriesAsync(int limit = 15, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);

            var result = new List<FolderJumpEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. 固定项优先
            foreach (var item in _items.Values.Where(i => i.IsPinned).OrderByDescending(i => i.LastAccessed))
            {
                if (seen.Add(item.Path))
                {
                    result.Add(new FolderJumpEntry(item.Path, item.Name, FolderJumpSource.Pinned, item.AccessCount, item.LastAccessed, true));
                }
            }

            // 2. 高频项 (访问 >= 3 次)
            foreach (var item in _items.Values.Where(i => !i.IsPinned && i.AccessCount >= 3).OrderByDescending(i => i.AccessCount).ThenByDescending(i => i.LastAccessed))
            {
                if (seen.Add(item.Path))
                {
                    result.Add(new FolderJumpEntry(item.Path, item.Name, FolderJumpSource.Frequent, item.AccessCount, item.LastAccessed, false));
                }
            }

            // 3. 最近访问项
            foreach (var item in _items.Values.Where(i => !seen.Contains(i.Path)).OrderByDescending(i => i.LastAccessed))
            {
                if (seen.Add(item.Path))
                {
                    result.Add(new FolderJumpEntry(item.Path, item.Name, FolderJumpSource.Recent, item.AccessCount, item.LastAccessed, false));
                }
            }

            return result.Take(limit).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<FolderJumpEntry>> SearchAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(query))
            {
                return await GetEntriesAsync(limit, ct).ConfigureAwait(false);
            }

            var scored = new List<(FolderJumpItem Item, int Score, FolderJumpSource Source)>();
            var matcher = PinyinMatcher.Instance;

            foreach (var item in _items.Values)
            {
                var match = matcher.Match(item.Name, query);
                int score = 0;
                if (match != null)
                {
                    score = match.Score;
                }
                else if (item.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    score = 40;
                }

                if (score > 0)
                {
                    // 结合访问频次和置顶权重提分
                    score += Math.Min(item.AccessCount * 2, 20);
                    if (item.IsPinned) score += 30;

                    var source = item.IsPinned ? FolderJumpSource.Pinned
                        : (item.AccessCount >= 3 ? FolderJumpSource.Frequent : FolderJumpSource.Recent);

                    scored.Add((item, score, source));
                }
            }

            return scored
                .OrderByDescending(s => s.Score)
                .ThenByDescending(s => s.Item.LastAccessed)
                .Take(limit)
                .Select(s => new FolderJumpEntry(s.Item.Path, s.Item.Name, s.Source, s.Item.AccessCount, s.Item.LastAccessed, s.Item.IsPinned))
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearHistoryAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _items.Clear();
            await SaveAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 1 ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
    }
}
