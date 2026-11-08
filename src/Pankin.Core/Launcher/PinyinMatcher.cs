using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Pankin.Core.Launcher;

/// <summary>
/// 匹配文本中的高亮区间（基于 UTF-16 下标）
/// </summary>
public readonly record struct TextSpan(int Start, int Length);

/// <summary>
/// 匹配结果，包含评分与高亮区间
/// </summary>
public sealed record NameMatch(int Score, IReadOnlyList<TextSpan> Spans);

/// <summary>
/// 拼音与多音字匹配器
/// </summary>
public sealed class PinyinMatcher
{
    private static readonly Lazy<PinyinMatcher> LazyInstance = new(() => new PinyinMatcher());
    public static PinyinMatcher Instance => LazyInstance.Value;

    private readonly Dictionary<int, string[]> _readings = new();

    public PinyinMatcher()
    {
        LoadPinyinData();
    }

    private void LoadPinyinData()
    {
        Stream? stream = null;
        try
        {
            var assembly = typeof(PinyinMatcher).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("pinyin.tsv", StringComparison.OrdinalIgnoreCase));

            if (resourceName != null)
            {
                stream = assembly.GetManifestResourceStream(resourceName);
            }

            if (stream == null)
            {
                var candidatePaths = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "Launcher", "Data", "pinyin.tsv"),
                    Path.Combine(AppContext.BaseDirectory, "LauncherData", "pinyin.tsv"),
                    Path.Combine(AppContext.BaseDirectory, "pinyin.tsv"),
                    Path.Combine(Directory.GetCurrentDirectory(), "src", "Pankin.Core", "Launcher", "Data", "pinyin.tsv"),
                    Path.Combine(Directory.GetCurrentDirectory(), "Launcher", "Data", "pinyin.tsv")
                };

                foreach (var path in candidatePaths)
                {
                    if (File.Exists(path))
                    {
                        stream = File.OpenRead(path);
                        break;
                    }
                }
            }

            if (stream == null) return;

            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length >= 2 && int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codePoint))
                {
                    var variants = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    _readings[codePoint] = variants;
                }
            }
        }
        catch
        {
            // 忽略读取错误，退化为无拼音匹配
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public SearchName Create(string name)
    {
        var tokens = new List<NameToken>();
        int offset = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune))
            {
                string[] variants = _readings.TryGetValue(rune.Value, out var r) ? r : [rune.ToString().ToLowerInvariant()];
                tokens.Add(new NameToken(offset, rune.Utf16SequenceLength, variants, variants.Select(s => s[..1]).Distinct().ToArray()));
            }
            offset += rune.Utf16SequenceLength;
        }
        return new SearchName(name, tokens.ToArray());
    }

    public SearchName CreateLocalized(string display, string simplified) => Create(simplified).WithDisplayName(display);

    /// <summary>
    /// 获取文本的首字母简拼（取第一个读音）
    /// </summary>
    public string GetInitials(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) continue;
            if (_readings.TryGetValue(rune.Value, out var readings) && readings.Length > 0 && readings[0].Length > 0)
            {
                sb.Append(readings[0][0]);
            }
            else
            {
                sb.Append(rune.ToString().ToLowerInvariant());
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 获取文本的拼音全拼序列（取第一个读音）
    /// </summary>
    public IReadOnlyList<string> GetPinyinTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) continue;
            if (_readings.TryGetValue(rune.Value, out var readings) && readings.Length > 0)
            {
                list.Add(readings[0]);
            }
            else
            {
                list.Add(rune.ToString().ToLowerInvariant());
            }
        }
        return list;
    }

    public NameMatch? Match(string target, string query)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(query)) return null;
        var search = Create(target);
        return search.Match(query);
    }
}

public sealed record NameToken(int Offset, int Length, string[] Full, string[] Initials);

public sealed class SearchName
{
    private readonly bool _phoneticHighlights;
    private readonly string _lower;
    private readonly NameToken[] _tokens;
    private readonly NameToken[] _wordInitials;

    public string Name { get; }
    public NameToken[] Tokens => _tokens;

    public SearchName(string name, NameToken[] tokens, bool phoneticHighlights = true)
    {
        Name = name;
        _tokens = tokens;
        _lower = name.ToLowerInvariant();
        _phoneticHighlights = phoneticHighlights;
        _wordInitials = tokens.Where((t, i) => i == 0 || !Latin(t) || !Latin(tokens[i - 1]) || tokens[i - 1].Offset + tokens[i - 1].Length != t.Offset).ToArray();
    }

    public SearchName WithDisplayName(string display) => new(display, _tokens, display.Length == Name.Length);

    private static bool Latin(NameToken t) => t.Length == 1 && t.Full.Length == 1 && t.Full[0].Length == 1 && t.Full[0][0] is >= 'a' and <= 'z';

    public NameMatch? Match(string query)
    {
        var q = query.ToLowerInvariant().Trim();
        if (q.Length == 0) return null;

        int i = _lower.IndexOf(q, StringComparison.Ordinal);
        if (i == 0) return new NameMatch(100, [new TextSpan(0, q.Length)]);

        var spans = Phonetic(q, true, true) ?? Phonetic(q, true, true, _wordInitials);
        if (spans != null) return new NameMatch(90, _phoneticHighlights ? spans : []);

        if (i >= 0) return new NameMatch(70, [new TextSpan(i, q.Length)]);

        spans = Phonetic(q, false, true);
        if (spans != null) return new NameMatch(60, _phoneticHighlights ? spans : []);

        spans = Phonetic(q, true, false) ?? Phonetic(q, true, false, _wordInitials);
        if (spans != null) return new NameMatch(45, _phoneticHighlights ? spans : []);

        spans = Phonetic(q, false, false);
        if (spans != null) return new NameMatch(35, _phoneticHighlights ? spans : []);

        var hits = new List<TextSpan>();
        int j = 0;
        for (int n = 0; n < _lower.Length && j < q.Length; n++)
        {
            if (_lower[n] == q[j])
            {
                hits.Add(new TextSpan(n, 1));
                j++;
            }
        }
        return j == q.Length && q.Length > 1 ? new NameMatch(10, Merge(hits)) : null;
    }

    private IReadOnlyList<TextSpan>? Phonetic(string q, bool initials, bool prefix, NameToken[]? selected = null)
    {
        var tokens = selected ?? _tokens;
        var failed = new HashSet<(int, int, int)>();

        for (int start = 0; start < tokens.Length; start++)
        {
            foreach (var reading in initials ? tokens[start].Initials : tokens[start].Full)
            {
                for (int offset = 0; offset < (prefix ? 1 : reading.Length); offset++)
                {
                    int take = Math.Min(reading.Length - offset, q.Length);
                    if (!q.AsSpan(0, take).SequenceEqual(reading.AsSpan(offset, take))) continue;

                    int end = take == q.Length ? start : Walk(start + 1, take);
                    if (end >= 0)
                    {
                        return Merge(tokens[start..(end + 1)].Select(t => new TextSpan(t.Offset, t.Length)));
                    }
                }
            }
            if (prefix) break;
        }
        return null;

        int Walk(int index, int position)
        {
            if (index >= tokens.Length || !failed.Add((index, position, initials ? 1 : 0))) return -1;
            foreach (var reading in initials ? tokens[index].Initials : tokens[index].Full)
            {
                int take = Math.Min(reading.Length, q.Length - position);
                if (!q.AsSpan(position, take).SequenceEqual(reading.AsSpan(0, take))) continue;
                if (position + take == q.Length) return index;
                int result = Walk(index + 1, position + take);
                if (result >= 0) return result;
            }
            return -1;
        }
    }

    private static TextSpan[] Merge(IEnumerable<TextSpan> source)
    {
        var output = new List<TextSpan>();
        foreach (var s in source)
        {
            if (output.Count > 0 && output[^1].Start + output[^1].Length == s.Start)
            {
                output[^1] = output[^1] with { Length = output[^1].Length + s.Length };
            }
            else
            {
                output.Add(s);
            }
        }
        return output.ToArray();
    }
}
