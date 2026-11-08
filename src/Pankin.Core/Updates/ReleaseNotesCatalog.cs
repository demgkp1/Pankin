using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pankin.Core.Updates;

internal sealed record NoteData(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("items")] NoteItemData[] Items);

internal sealed record NoteItemData(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("highlight")] bool Highlight = false);

/// <summary>
/// 历史与在线版本更新说明目录服务实现
/// </summary>
public sealed class ReleaseNotesCatalog : IReleaseNotesCatalog
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public IReadOnlyList<ReleaseNotes> All { get; }

    public ReleaseNotesCatalog(IEnumerable<ReleaseNotes> entries)
    {
        All = entries
            .OrderByDescending(n => n.Version, Comparer<string>.Create(VersionCompare.CompareVersions))
            .ToArray();
    }

    public ReleaseNotesCatalog(string? jsonContent = null, Action<string>? log = null)
        : this(ParseJson(jsonContent, log))
    {
    }

    public ReleaseNotesCatalog(IEnumerable<(string Name, Func<Stream> Open)> files, Action<string>? log = null)
        : this(ParseFiles(files, log))
    {
    }

    public ReleaseNotes? Find(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        try
        {
            return All.FirstOrDefault(n => VersionCompare.CompareVersions(n.Version, version) == 0);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 GitHub Release 正文中的结构化 Markdown 片段（## 新增 / ## 改进 / ## 修复）
    /// </summary>
    public static ReleaseNotes? ParseMarkdown(string body, string version, DateTimeOffset published)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        ReleaseNoteKind? currentKind = null;
        var items = new List<ReleaseNoteItem>();

        foreach (string raw in body.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
        {
            string line = raw.Trim();
            if (line.StartsWith('#'))
            {
                currentKind = line switch
                {
                    "## 新增" or "### 新增" or "## New" or "### New" => ReleaseNoteKind.New,
                    "## 改进" or "### 改进" or "## Improved" or "### Improved" => ReleaseNoteKind.Improved,
                    "## 修复" or "### 修复" or "## Fixed" or "### Fixed" => ReleaseNoteKind.Fixed,
                    _ => null
                };
                continue;
            }

            if (currentKind is { } kind && line.Length > 2 && (line[0] == '-' || line[0] == '*') && char.IsWhiteSpace(line[1]))
            {
                string text = line[2..].Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    bool highlight = text.StartsWith("[highlight]", StringComparison.OrdinalIgnoreCase) ||
                                     text.StartsWith("★") || text.StartsWith("【亮点】");
                    if (highlight)
                    {
                        text = text.Replace("[highlight]", "", StringComparison.OrdinalIgnoreCase)
                                   .TrimStart('★', ' ')
                                   .Replace("【亮点】", "").Trim();
                    }

                    items.Add(new ReleaseNoteItem(kind, text, highlight));
                }
            }
        }

        return items.Count == 0 ? null : new ReleaseNotes(version, DateOnly.FromDateTime(published.Date), items);
    }

    private static List<ReleaseNotes> ParseJson(string? jsonContent, Action<string>? log)
    {
        var result = new List<ReleaseNotes>();
        if (string.IsNullOrWhiteSpace(jsonContent)) return result;

        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var entries = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToArray()
                : new[] { doc.RootElement };

            foreach (var elem in entries)
            {
                try
                {
                    var data = elem.Deserialize<NoteData>(SerializerOptions);
                    if (data == null || !VersionCompare.IsValid(data.Version)) continue;

                    var items = (data.Items ?? Array.Empty<NoteItemData>())
                        .Where(i => !string.IsNullOrWhiteSpace(i.Text))
                        .Select(i => new ReleaseNoteItem(
                            i.Kind?.ToLowerInvariant() switch
                            {
                                "new" => ReleaseNoteKind.New,
                                "improved" => ReleaseNoteKind.Improved,
                                "fixed" => ReleaseNoteKind.Fixed,
                                _ => ReleaseNoteKind.Improved
                            },
                            i.Text.Trim(),
                            i.Highlight))
                        .ToArray();

                    if (result.Any(n => VersionCompare.CompareVersions(n.Version, data.Version) == 0))
                    {
                        continue;
                    }

                    DateOnly? date = DateOnly.TryParse(data.Date, out var parsedDate) ? parsedDate : null;
                    result.Add(new ReleaseNotes(data.Version, date, items));
                }
                catch (Exception ex)
                {
                    log?.Invoke($"解析条目异常: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"解析 JSON 异常: {ex.Message}");
        }

        return result;
    }

    private static List<ReleaseNotes> ParseFiles(IEnumerable<(string Name, Func<Stream> Open)> files, Action<string>? log)
    {
        var result = new List<ReleaseNotes>();
        foreach (var (name, open) in files)
        {
            try
            {
                using var stream = open();
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var notes = ParseJson(json, log);
                foreach (var note in notes)
                {
                    if (!result.Any(n => VersionCompare.CompareVersions(n.Version, note.Version) == 0))
                    {
                        result.Add(note);
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"读取文件 {name} 失败: {ex.Message}");
            }
        }
        return result;
    }
}
