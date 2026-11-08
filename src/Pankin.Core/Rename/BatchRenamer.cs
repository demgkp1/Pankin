using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Pankin.Core.Rename;

/// <summary>
/// 跨平台批量重命名引擎实现：规则叠加、秒级实时预览缓存、环状互换安全事务与撤销。
/// 纯 C# 实现，不依赖任何特定平台专属 API。
/// </summary>
public sealed class BatchRenamer : IBatchRenamer
{
    private static readonly NaturalStringComparer Natural = NaturalStringComparer.OrdinalIgnoreCase;
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    private static readonly char[] InvalidChars = new char[] { (char)92, (char)47, (char)58, (char)42, (char)63, (char)34, (char)60, (char)62, (char)124 };

    public Task<RenameFolderListing> ListFolderAsync(string folder, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(folder);

        var attributes = File.GetAttributes(root);
        if (!attributes.HasFlag(FileAttributes.Directory))
            throw new IOException("指定路径不是文件夹");
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("不支持链接文件夹");

        var direct = new List<string>();
        var nested = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        bool truncated = false;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
        };

        while (pending.TryPop(out string? current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var children = new List<string>();
            try
            {
                foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        children.Add(entry.FullName);
                        continue;
                    }

                    if (direct.Count + nested.Count == IBatchRenamer.MaxFolderFiles)
                    {
                        truncated = true;
                        break;
                    }

                    (current == root ? direct : nested).Add(entry.FullName);
                }
            }
            catch (UnauthorizedAccessException) when (current != root) { }

            if (truncated) break;

            children.Sort(Natural);
            for (int i = children.Count - 1; i >= 0; i--)
            {
                pending.Push(children[i]);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        direct.Sort((a, b) => Natural.Compare(Path.GetFileName(a), Path.GetFileName(b)));
        nested.Sort((a, b) =>
        {
            int parent = Natural.Compare(Path.GetDirectoryName(a)!, Path.GetDirectoryName(b)!);
            return parent != 0 ? parent : Natural.Compare(Path.GetFileName(a), Path.GetFileName(b));
        });

        return new RenameFolderListing(direct, nested, truncated);
    }, cancellationToken);

    private sealed record Metadata(bool Exists, bool Directory, DateTime Created, DateTime Modified);
    private sealed record Move(string Before, string After, bool RestoresOriginal = false);

    private readonly SemaphoreSlim _gate = new(1);
    private string[] _batch = [];
    private Metadata[] _metadata = [];
    private HashSet<string> _occupied = new(Paths);
    private readonly Dictionary<string, List<Move>> _undo = [];

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static string Target(string path, string name) => Path.Combine(Path.GetDirectoryName(path)!, name);

    public async Task<IReadOnlyList<RenamePreview>> PreviewAsync(IReadOnlyList<string> paths, IReadOnlyList<RenameRule> rules, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Preview(paths, rules, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<RenamePreview> Preview(IReadOnlyList<string> paths, IReadOnlyList<RenameRule> rules, CancellationToken token)
    {
        if (!_batch.SequenceEqual(paths, StringComparer.Ordinal))
        {
            var info = new List<Metadata>();
            var entries = new HashSet<string>(Paths);
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                bool dir = Directory.Exists(path);
                FileSystemInfo file = dir ? new DirectoryInfo(path) : new FileInfo(path);
                file.Refresh();
                info.Add(new(file.Exists, dir, file.CreationTime, file.LastWriteTime));
            }

            foreach (var parent in paths.Select(Path.GetDirectoryName).Distinct(Paths))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (parent != null && Directory.Exists(parent))
                    {
                        entries.UnionWith(Directory.EnumerateFileSystemEntries(parent));
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            _batch = paths.ToArray();
            _metadata = info.ToArray();
            _occupied = entries;
        }

        var active = rules.Where(r => r.Enabled).ToArray();
        var regex = new Dictionary<RenameRule, Regex>();
        try
        {
            foreach (var rule in active.Where(r => r.Kind == RenameRuleKind.Replace && r.Regex))
            {
                regex[rule] = new Regex(rule.Find, RegexOptions.CultureInvariant | (rule.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(100));
            }
        }
        catch (ArgumentException)
        {
            return paths.Select(p => new RenamePreview(p, Path.GetFileName(p), RenameIssue.BadPattern)).ToArray();
        }

        var result = new RenamePreview[paths.Count];
        var sources = new HashSet<string>(paths, Paths);
        var targets = new Dictionary<string, List<int>>(Paths);

        for (int i = 0; i < paths.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            string path = paths[i];
            string original = Path.GetFileName(path);
            string name = original;
            var m = _metadata[i];
            RenameIssue issue;

            try
            {
                name = Transform(original, m.Directory, m.Created, m.Modified, i, active, regex);
                issue = NameIssue(name, Target(path, name));
            }
            catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException or FormatException)
            {
                return paths.Select(p => new RenamePreview(p, Path.GetFileName(p), RenameIssue.BadPattern)).ToArray();
            }

            string target = Target(path, name);
            if (!m.Exists)
            {
                issue = RenameIssue.Missing;
            }
            else if (issue == RenameIssue.None && _occupied.Contains(target) && !sources.Contains(target))
            {
                issue = RenameIssue.Exists;
            }
            else if (issue == RenameIssue.None && name == original)
            {
                issue = RenameIssue.Unchanged;
            }

            result[i] = new RenamePreview(path, name, issue);
            if (!targets.TryGetValue(target, out var list))
            {
                targets[target] = list = [];
            }
            list.Add(i);
        }

        foreach (var list in targets.Values.Where(v => v.Count > 1))
        {
            foreach (int i in list)
            {
                if (result[i].Issue is RenameIssue.None or RenameIssue.Unchanged)
                {
                    result[i] = result[i] with { Issue = RenameIssue.Duplicate };
                }
            }
        }

        return result;
    }

    public static string Transform(string name, bool directory, DateTime created, DateTime modified, int ordinal, IReadOnlyList<RenameRule> rules, Dictionary<RenameRule, Regex>? patterns = null)
    {
        string ext = directory ? "" : Path.GetExtension(name);
        string stem = ext.Length == 0 ? name : name[..^ext.Length];

        foreach (var r in rules)
        {
            if (!r.Enabled) continue;

            switch (r.Kind)
            {
                case RenameRuleKind.Replace:
                    if (r.Regex)
                    {
                        var rx = patterns?.GetValueOrDefault(r) ?? new Regex(r.Find, RegexOptions.CultureInvariant | (r.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(100));
                        stem = rx.Replace(stem, r.Text);
                    }
                    else if (r.Find.Length > 0)
                    {
                        stem = stem.Replace(r.Find, r.Text, r.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                    }
                    break;

                case RenameRuleKind.Insert:
                    stem = Insert(stem, r.Text, r);
                    break;

                case RenameRuleKind.Number:
                    string number = ((long)r.Start + (long)ordinal * r.Step).ToString("D" + Math.Clamp(r.Digits, 1, 99), CultureInfo.InvariantCulture);
                    stem = Insert(stem, r.Position == RenamePosition.Start ? number + r.Separator : r.Separator + number, r);
                    break;

                case RenameRuleKind.Date:
                    stem = Insert(stem, (r.DateSource == RenameDateSource.Created ? created : modified).ToString(r.DateFormat, CultureInfo.InvariantCulture) + r.Separator, r);
                    break;

                case RenameRuleKind.Remove:
                    if (stem.Length == 0) break;
                    int index = Math.Clamp(r.Index, 1, stem.Length);
                    int count = Math.Max(0, r.Count);
                    int start = r.FromEnd ? Math.Max(0, stem.Length - index - count + 1) : index - 1;
                    int length = r.FromEnd ? Math.Min(count, stem.Length - index + 1) : Math.Min(count, stem.Length - start);
                    stem = stem.Remove(start, length);
                    break;

                case RenameRuleKind.Case:
                    stem = ChangeCase(stem, r.Case);
                    if (r.IncludeExtension) ext = ChangeCase(ext, r.Case);
                    break;

                case RenameRuleKind.Extension:
                    if (!directory) ext = r.Text.Length == 0 ? "" : "." + r.Text;
                    break;
            }
        }

        return stem + ext;
    }

    private static string ChangeCase(string value, RenameCase kind) => kind switch
    {
        RenameCase.Upper => value.ToUpperInvariant(),
        RenameCase.Title => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant()),
        _ => value.ToLowerInvariant()
    };

    private static string Insert(string value, string text, RenameRule r)
    {
        int index = Math.Clamp(r.Index, 1, Math.Max(1, value.Length));
        int at = r.Position switch
        {
            RenamePosition.Start => 0,
            RenamePosition.End => value.Length,
            _ => value.Length == 0 ? 0 : r.FromEnd ? value.Length - index + 1 : index - 1
        };
        return value.Insert(at, text);
    }

    public static RenameIssue NameIssue(string name, string path)
    {
        if (name.Length == 0) return RenameIssue.Empty;
        if (name.IndexOfAny(InvalidChars) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith((char)32) || name.EndsWith((char)46))
            return RenameIssue.Invalid;

        string first = name.Split((char)46)[0].TrimEnd((char)32).ToUpperInvariant();
        if (first is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || Regex.IsMatch(first, @"^(COM|LPT)[1-9¹²³]$", RegexOptions.CultureInvariant))
            return RenameIssue.Invalid;

        return name.Length > 255 || path.Length >= 32767 ? RenameIssue.TooLong : RenameIssue.None;
    }

    public async Task<RenameResult> ApplyAsync(IReadOnlyList<RenamePreview> plan, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Apply(plan, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private RenameResult Apply(IReadOnlyList<RenamePreview> plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var failed = new List<RenameFailure>();
        var journal = new List<Move>();
        int renamed = 0;

        foreach (var layer in plan.Where(p => p.Issue != RenameIssue.Unchanged).GroupBy(p => p.Path.Count(c => c == Path.DirectorySeparatorChar)).OrderByDescending(g => g.Key))
        {
            var items = layer.ToArray();
            var parent = Enumerable.Range(0, items.Length).ToArray();
            int Root(int i)
            {
                while (parent[i] != i)
                {
                    parent[i] = parent[parent[i]];
                    i = parent[i];
                }
                return i;
            }

            var sources = new Dictionary<string, int>(Paths);
            for (int i = 0; i < items.Length; i++) sources.TryAdd(items[i].Path, i);
            for (int i = 0; i < items.Length; i++)
            {
                if (sources.TryGetValue(Target(items[i].Path, items[i].NewName), out int other))
                {
                    parent[Root(i)] = Root(other);
                }
            }

            foreach (var component in Enumerable.Range(0, items.Length).GroupBy(Root))
            {
                var staged = new List<(RenamePreview Item, string Temp, string Target)>();
                var changes = new List<Move>();
                string? error = null;
                string? failedPath = null;

                foreach (int index in component)
                {
                    var p = items[index];
                    var target = Target(p.Path, p.NewName);
                    if (token.IsCancellationRequested)
                    {
                        error = "操作已被取消";
                        failedPath = p.Path;
                        break;
                    }

                    if (p.Issue != RenameIssue.None || NameIssue(p.NewName, target) != RenameIssue.None)
                    {
                        error = "预览状态不合法或存在冲突";
                        failedPath = p.Path;
                        break;
                    }

                    string temp = Target(p.Path, ".pankin-rename-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        MovePath(p.Path, temp);
                        staged.Add((p, temp, target));
                        changes.Add(new(p.Path, temp, true));
                    }
                    catch (Exception e) when (IsFileError(e))
                    {
                        error = e.Message;
                        failedPath = p.Path;
                        break;
                    }
                }

                if (error == null)
                {
                    foreach (var s in staged)
                    {
                        try
                        {
                            MovePath(s.Temp, s.Target);
                            changes.Add(new(s.Temp, s.Target));
                        }
                        catch (Exception e) when (IsFileError(e))
                        {
                            error = e.Message;
                            failedPath = s.Item.Path;
                            break;
                        }
                    }
                }

                if (error == null)
                {
                    journal.AddRange(changes);
                    renamed += staged.Count;
                    continue;
                }

                foreach (int index in component)
                {
                    failed.Add(new(items[index].Path, items[index].Path == failedPath ? error : "关联文件失败，事务已回滚"));
                }

                for (int i = changes.Count - 1; i >= 0; i--)
                {
                    var move = changes[i];
                    try
                    {
                        MovePath(move.After, move.Before);
                        changes.RemoveAt(i);
                    }
                    catch (Exception e) when (IsFileError(e))
                    {
                        failed.Add(new(move.After, e.Message));
                    }
                }

                journal.AddRange(changes);
            }
        }

        _batch = [];
        string? id = journal.Count == 0 ? null : Guid.NewGuid().ToString("N");
        if (id != null) _undo[id] = journal;
        return new RenameResult(renamed, failed, id);
    }

    private static bool IsFileError(Exception e) => e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static void MovePath(string from, string to)
    {
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to);
        }
    }

    public async Task<RenameResult> UndoAsync(string undoId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_undo.TryGetValue(undoId, out var journal))
                    return new RenameResult(0, [], null);

                var failures = new List<RenameFailure>();
                int restored = 0;

                for (int i = journal.Count - 1; i >= 0; i--)
                {
                    var move = journal[i];
                    try
                    {
                        MovePath(move.After, move.Before);
                        journal.RemoveAt(i);
                        if (move.RestoresOriginal) restored++;
                    }
                    catch (Exception e) when (IsFileError(e))
                    {
                        failures.Add(new(move.After, e.Message));
                    }
                }

                _batch = [];
                if (journal.Count == 0) _undo.Remove(undoId);
                return new RenameResult(restored, failures, journal.Count > 0 ? undoId : null);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
