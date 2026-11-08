using System.Text.Json;
using System.Text.Json.Serialization;
using Pankin.Core.Abstractions;
using Pankin.Core.Settings;

namespace Pankin.Core.Launcher;

[JsonSerializable(typeof(List<string>))]
internal partial class TerminalHistoryJsonContext : JsonSerializerContext
{
}

/// <summary>
/// 终端历史命令持久化管理器
/// </summary>
public sealed class TerminalHistory : ITerminalHistory
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private List<string> _history = [];

    public TerminalHistory(string? directoryPath = null)
    {
        var baseDir = directoryPath ?? Path.GetDirectoryName(SettingsStore.GetDefaultConfigPath())!;
        _filePath = Path.Combine(baseDir, "terminal-history.json");
        Load();
    }

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    _history = (JsonSerializer.Deserialize(json, TerminalHistoryJsonContext.Default.ListString) ?? [])
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.Ordinal)
                        .Take(20)
                        .ToList();
                }
            }
            catch
            {
                _history = [];
            }
        }
    }

    public IReadOnlyList<string> Match(string prefix)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                return _history.Take(6).ToArray();

            return _history
                .Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Take(6)
                .ToArray();
        }
    }

    public void Record(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || char.IsWhiteSpace(raw[0])) return;

        lock (_lock)
        {
            _history.RemoveAll(s => string.Equals(s, raw, StringComparison.Ordinal));
            _history.Insert(0, raw);
            if (_history.Count > 20)
            {
                _history.RemoveRange(20, _history.Count - 20);
            }
            SaveCore();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _history.Clear();
            SaveCore();
        }
    }

    private void SaveCore()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(_history, TerminalHistoryJsonContext.Default.ListString);
            File.WriteAllText(tmp, json);
            File.Move(tmp, _filePath, true);
        }
        catch
        {
            // 忽略写入故障
        }
    }
}

/// <summary>
/// 终端前缀 `>` 快速命令执行与历史补全服务
/// </summary>
public sealed class TerminalService
{
    private static readonly string[] CommonLinuxPrograms =
    [
        "bash", "zsh", "sh", "top", "htop", "btop", "git", "docker", "systemctl",
        "journalctl", "curl", "wget", "ssh", "ping", "nano", "vim", "nvim",
        "df", "free", "uname", "ip", "ifconfig", "netstat", "grep", "find",
        "cat", "chmod", "chown", "mkdir", "rm", "cp", "mv", "tar", "unzip"
    ];

    private readonly ILauncherActionHost _actionHost;
    private readonly TerminalHistory _history;

    public ITerminalHistory History => _history;

    public TerminalService(ILauncherActionHost actionHost, string? configDir = null)
    {
        _actionHost = actionHost;
        _history = new TerminalHistory(configDir);
    }

    public LauncherResult Search(string query)
    {
        if (!query.StartsWith('>')) return new LauncherResult([], false);

        string raw = query[1..];
        string command = raw.TrimStart();
        var rows = new List<LauncherItem>();

        if (string.IsNullOrWhiteSpace(command))
        {
            rows.Add(new LauncherItem(
                "terminal:hint",
                LauncherItemKind.Action,
                LauncherGroups.Terminal,
                "输入要在终端里运行的命令",
                "例如 > top、> git status",
                new LauncherIcon("terminal")));
        }
        else
        {
            rows.Add(new LauncherItem(
                "terminal:run:" + raw,
                LauncherItemKind.Action,
                LauncherGroups.Terminal,
                "在终端中运行",
                command,
                new LauncherIcon("terminal"),
                Hotkey: "Enter",
                Score: 100));

            var historyMatches = _history.Match(command);
            var builtinMatches = CommonLinuxPrograms
                .Where(p => p.StartsWith(command, StringComparison.OrdinalIgnoreCase));

            var completions = historyMatches
                .Concat(builtinMatches)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(s => !string.Equals(s, command, StringComparison.OrdinalIgnoreCase))
                .Take(6)
                .Select(s => new LauncherItem(
                    "complete:" + s,
                    LauncherItemKind.Action,
                    LauncherGroups.Completion,
                    s,
                    "终端补全",
                    new LauncherIcon("terminal"),
                    Score: 90));

            rows.AddRange(completions);
        }

        return new LauncherResult(rows, false);
    }

    public static bool Owns(LauncherItem item) =>
        item.Id.StartsWith("terminal:", StringComparison.Ordinal) ||
        item.Id.StartsWith("complete:", StringComparison.Ordinal);

    public async Task ExecuteAsync(LauncherItem item, bool elevated, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (item.Id == "terminal:hint") return;

        string raw = item.Id.StartsWith("complete:", StringComparison.Ordinal)
            ? item.Id[9..]
            : item.Id.StartsWith("terminal:run:", StringComparison.Ordinal)
                ? item.Id[13..]
                : throw new NotSupportedException(item.Id);

        string command = raw.TrimStart();
        if (string.IsNullOrWhiteSpace(command)) return;

        await _actionHost.RunInTerminalAsync(command, elevated, null, token).ConfigureAwait(false);
        _history.Record(raw);
    }
}
