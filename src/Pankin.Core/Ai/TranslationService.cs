using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace Pankin.Core.Ai;

/// <summary>
/// 核心翻译服务：提供语言识别、目标语言智能推断、提示词模板格式化、多轮流式翻译及内存缓存。
/// </summary>
public sealed class TranslationService
{
    public const string DefaultPromptTemplate = "把下面的文字翻译成{lang}。只输出译文，不要多余解释；保留原文的换行和格式：\n\n{text}";

    public static readonly IReadOnlyList<(string Code, string Name)> SupportedLanguages =
    [
        ("zh", "中文"),
        ("en", "英文"),
        ("ja", "日文"),
        ("ko", "韩文"),
        ("fr", "法文"),
        ("de", "德文"),
        ("es", "西班牙文"),
        ("ru", "俄文")
    ];

    private readonly IAiService _aiService;
    private readonly int _maxCacheEntries;
    private readonly ConcurrentDictionary<(string Text, string Target), string> _cache = new();
    private readonly Queue<(string Text, string Target)> _cacheOrder = new();
    private readonly object _cacheLock = new();

    public int CacheCount => _cache.Count;

    public TranslationService(IAiService aiService, int maxCacheEntries = 500)
    {
        _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));
        _maxCacheEntries = maxCacheEntries > 0 ? maxCacheEntries : 500;
    }

    /// <summary>
    /// 判断文本是否以中文为主（汉字数量充足且在总字母数量中占显著比例）。
    /// </summary>
    public static bool IsChinese(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;

        int han = 0;
        int letters = 0;
        foreach (var r in s.EnumerateRunes())
        {
            if (r.Value is (>= 0x3400 and <= 0x4dbf) or (>= 0x4e00 and <= 0x9fff) or (>= 0xf900 and <= 0xfaff) or (>= 0x20000 and <= 0x323af))
            {
                han++;
            }
            else if (Rune.IsLetter(r))
            {
                letters++;
            }
        }

        return han > 0 && han >= letters / 4;
    }

    /// <summary>
    /// 识别文本语言代码（"zh", "ja", "ko", "en", "other"）。
    /// </summary>
    public static string DetectLanguage(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "en";

        if (IsChinese(s)) return "zh";

        int totalLetters = 0;
        int asciiLetters = 0;
        foreach (var r in s.EnumerateRunes())
        {
            if (r.Value is >= 0x3040 and <= 0x30ff) return "ja"; // 日文平假名与片假名
            if (r.Value is >= 0xac00 and <= 0xd7af) return "ko"; // 韩文谚文

            if (Rune.IsLetter(r))
            {
                totalLetters++;
                if (r.IsAscii) asciiLetters++;
            }
        }

        if (totalLetters > 0 && asciiLetters * 2 >= totalLetters)
        {
            return "en";
        }

        return "other";
    }

    /// <summary>
    /// 获取语言代码的友好展示名称。
    /// </summary>
    public static string GetLanguageName(string code) => code.ToLowerInvariant() switch
    {
        "zh" or "zh-cn" or "zh-hans" => "中文",
        "zh-tw" or "zh-hk" or "zh-hant" => "繁体中文",
        "en" or "en-us" => "英文",
        "ja" => "日文",
        "ko" => "韩文",
        "fr" => "法文",
        "de" => "德文",
        "es" => "西班牙文",
        "ru" => "俄文",
        _ => "中文"
    };

    /// <summary>
    /// 根据原文语言和默认配置自动推断目标语言（中文译为英文，其余译为设定目标或中文）。
    /// </summary>
    public static string ResolveTargetLanguage(string text, string configuredTarget = "auto")
    {
        if (IsChinese(text))
        {
            return "en";
        }

        if (string.IsNullOrWhiteSpace(configuredTarget) || configuredTarget.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return "zh";
        }

        return configuredTarget.ToLowerInvariant();
    }

    /// <summary>
    /// 格式化翻译提示词模板，安全替换 {lang} 与 {text}。
    /// </summary>
    public static string BuildPrompt(string template, string text, string targetLanguageName)
    {
        var span = template.AsSpan();
        var result = new StringBuilder(template.Length + text.Length + targetLanguageName.Length);

        for (int i = 0; i < span.Length;)
        {
            if (span[i..].StartsWith("{text}".AsSpan()))
            {
                result.Append(text);
                i += 6;
            }
            else if (span[i..].StartsWith("{lang}".AsSpan()))
            {
                result.Append(targetLanguageName);
                i += 6;
            }
            else
            {
                result.Append(span[i]);
                i++;
            }
        }

        return result.ToString();
    }

    public bool TryGetCached(string text, string targetLanguage, [NotNullWhen(true)] out string? cached)
    {
        return _cache.TryGetValue((text.Trim(), targetLanguage.ToLowerInvariant()), out cached);
    }

    public void CacheTranslation(string text, string targetLanguage, string result)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(result))
            return;

        var key = (text.Trim(), targetLanguage.ToLowerInvariant());
        lock (_cacheLock)
        {
            if (_cache.ContainsKey(key))
            {
                _cache[key] = result;
                return;
            }

            while (_cache.Count >= _maxCacheEntries && _cacheOrder.Count > 0)
            {
                var oldest = _cacheOrder.Dequeue();
                _cache.TryRemove(oldest, out _);
            }

            _cache[key] = result;
            _cacheOrder.Enqueue(key);
        }
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            _cacheOrder.Clear();
        }
    }

    /// <summary>
    /// 流式返回翻译文本，命中缓存时直接 yield 缓存结果。
    /// </summary>
    public async IAsyncEnumerable<string> StreamTranslateAsync(
        string text,
        string targetLanguage = "auto",
        string? modelOverride = null,
        string? promptTemplate = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var cleanText = text.Trim();
        var resolvedTarget = ResolveTargetLanguage(cleanText, targetLanguage);

        if (TryGetCached(cleanText, resolvedTarget, out var cached))
        {
            yield return cached;
            yield break;
        }

        var langName = GetLanguageName(resolvedTarget);
        var template = promptTemplate ?? DefaultPromptTemplate;
        var prompt = BuildPrompt(template, cleanText, langName);

        var messages = new List<AiChatMessage>
        {
            new(AiChatRole.User, prompt)
        };

        var config = _aiService.Config;
        var model = modelOverride ?? config.TranslationModel ?? config.Model;
        var sb = new StringBuilder();

        await foreach (var chunk in _aiService.StreamChatAsync(messages, model, ct).ConfigureAwait(false))
        {
            if (ct.IsCancellationRequested) break;
            sb.Append(chunk);
            yield return chunk;
        }

        if (sb.Length > 0 && !ct.IsCancellationRequested)
        {
            CacheTranslation(cleanText, resolvedTarget, sb.ToString());
        }
    }

    /// <summary>
    /// 单次异步执行翻译并返回完整结果字符串。
    /// </summary>
    public async Task<string> TranslateAsync(
        string text,
        string targetLanguage = "auto",
        string? modelOverride = null,
        string? promptTemplate = null,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in StreamTranslateAsync(text, targetLanguage, modelOverride, promptTemplate, ct).ConfigureAwait(false))
        {
            sb.Append(chunk);
        }
        return sb.ToString();
    }
}
