using System.Runtime.CompilerServices;
using Pankin.Core.Ai;
using Xunit;

namespace Pankin.Core.Tests.Ai;

public class TranslationServiceTests
{
    private sealed class MockAiService : IAiService
    {
        public AiConfig Config { get; private set; } = new();
        public int ChatCallCount { get; private set; }
        public List<AiChatMessage> LastMessages { get; } = [];

        public void UpdateConfig(AiConfig config) => Config = config;

        public async IAsyncEnumerable<string> StreamChatAsync(
            IReadOnlyList<AiChatMessage> messages,
            string? modelOverride = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ChatCallCount++;
            LastMessages.Clear();
            LastMessages.AddRange(messages);

            await Task.Yield();
            yield return "Translated: ";
            yield return "Result";
        }

        public IAsyncEnumerable<string> StreamTranslateAsync(
            string text,
            string targetLanguage = "auto",
            string? modelOverride = null,
            CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    [Theory]
    [InlineData("你好，世界！", true)]
    [InlineData("Hello World", false)]
    [InlineData("这是中文测试 Chinese words", true)]
    [InlineData("Just a single 汉 in a very long English paragraph testing the boundary", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsChinese_DetectsChineseCorrectly(string? input, bool expected)
    {
        Assert.Equal(expected, TranslationService.IsChinese(input!));
    }

    [Theory]
    [InlineData("你好世界", "zh")]
    [InlineData("こんにちは", "ja")]
    [InlineData("안녕하세요", "ko")]
    [InlineData("Hello, this is pure english text", "en")]
    public void DetectLanguage_IdentifiesLanguages(string input, string expectedCode)
    {
        Assert.Equal(expectedCode, TranslationService.DetectLanguage(input));
    }

    [Fact]
    public void ResolveTargetLanguage_ChineseResolvesToEnglish()
    {
        var target = TranslationService.ResolveTargetLanguage("这是一段中文测试内容", "auto");
        Assert.Equal("en", target);
    }

    [Fact]
    public void ResolveTargetLanguage_EnglishResolvesToChineseWhenAuto()
    {
        var target = TranslationService.ResolveTargetLanguage("This is an English test content", "auto");
        Assert.Equal("zh", target);
    }

    [Fact]
    public void ResolveTargetLanguage_ExplicitTarget_UsedWhenNotChinese()
    {
        var target = TranslationService.ResolveTargetLanguage("This is an English test content", "ja");
        Assert.Equal("ja", target);
    }

    [Fact]
    public void BuildPrompt_ReplacesPlaceholdersCorrectly()
    {
        var template = "Translate to {lang}:\n{text}";
        var text = "Hello world";
        var lang = "中文";

        var prompt = TranslationService.BuildPrompt(template, text, lang);

        Assert.Equal("Translate to 中文:\nHello world", prompt);
    }

    [Fact]
    public void BuildPrompt_DoesNotDoubleReplaceUserInput()
    {
        var template = "Translate to {lang}:\n{text}";
        var text = "Some text containing {lang} inside it";
        var lang = "中文";

        var prompt = TranslationService.BuildPrompt(template, text, lang);

        Assert.Equal("Translate to 中文:\nSome text containing {lang} inside it", prompt);
    }

    [Fact]
    public async Task StreamTranslateAsync_CachesResult_SecondCallHitsCache()
    {
        var mockAi = new MockAiService();
        var service = new TranslationService(mockAi);

        var text = "Hello world";
        var results1 = new List<string>();
        await foreach (var chunk in service.StreamTranslateAsync(text, "zh"))
        {
            results1.Add(chunk);
        }

        Assert.Equal(1, mockAi.ChatCallCount);
        Assert.Equal("Translated: Result", string.Concat(results1));
        Assert.Equal(1, service.CacheCount);

        // 第二次请求相同文本和目标语言：直接走缓存，不应调用 AI 服务
        var results2 = new List<string>();
        await foreach (var chunk in service.StreamTranslateAsync(text, "zh"))
        {
            results2.Add(chunk);
        }

        Assert.Equal(1, mockAi.ChatCallCount);
        Assert.Equal("Translated: Result", string.Concat(results2));
    }

    [Fact]
    public async Task TranslateAsync_ReturnsFullString()
    {
        var mockAi = new MockAiService();
        var service = new TranslationService(mockAi);

        var result = await service.TranslateAsync("Hello world", "zh");

        Assert.Equal("Translated: Result", result);
    }

    [Fact]
    public void CacheEviction_RespectsMaxEntries()
    {
        var mockAi = new MockAiService();
        var service = new TranslationService(mockAi, maxCacheEntries: 2);

        service.CacheTranslation("text1", "zh", "result1");
        service.CacheTranslation("text2", "zh", "result2");
        Assert.Equal(2, service.CacheCount);

        service.CacheTranslation("text3", "zh", "result3");
        Assert.Equal(2, service.CacheCount);

        // 最早的 text1 应被淘汰
        Assert.False(service.TryGetCached("text1", "zh", out _));
        Assert.True(service.TryGetCached("text2", "zh", out _));
        Assert.True(service.TryGetCached("text3", "zh", out _));
    }

    [Fact]
    public void ClearCache_RemovesAllEntries()
    {
        var mockAi = new MockAiService();
        var service = new TranslationService(mockAi);

        service.CacheTranslation("test", "zh", "result");
        Assert.Equal(1, service.CacheCount);

        service.ClearCache();
        Assert.Equal(0, service.CacheCount);
    }
}
